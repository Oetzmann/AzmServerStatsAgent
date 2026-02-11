using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Text;
using System.Timers;
using Newtonsoft.Json;

namespace AzmServerStatsAgent
{
    public class ServerStatsService : ServiceBase
    {
        private Timer _timer;
        private string _serverName;
        private string _connectionString;
        private string _samplesDirectory;
        private int _intervalSeconds;
        private DateTime _currentHour;
        private string _currentHourFilePath;
        private readonly List<SampleRecord> _currentHourSamples = new List<SampleRecord>();

        public ServerStatsService()
        {
            ServiceName = "AzmServerStatsAgent";
        }

        protected override void OnStart(string[] args)
        {
            _serverName = ConfigurationManager.AppSettings["ServerName"];
            if (string.IsNullOrWhiteSpace(_serverName))
                _serverName = Environment.MachineName;

            _connectionString = ConfigurationManager.ConnectionStrings["StatsDb"]?.ConnectionString;
            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                LogEvent("ConnectionString 'StatsDb' fehlt in App.config.");
                return;
            }

            string dir = ConfigurationManager.AppSettings["SamplesDirectory"];
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AzmServerStats");
            _samplesDirectory = dir;
            try
            {
                if (!Directory.Exists(_samplesDirectory))
                    Directory.CreateDirectory(_samplesDirectory);
            }
            catch (Exception ex)
            {
                LogEvent("Samples-Verzeichnis konnte nicht erstellt werden: " + ex.Message);
                return;
            }

            string intervalStr = ConfigurationManager.AppSettings["IntervalSeconds"];
            if (!int.TryParse(intervalStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out _intervalSeconds) || _intervalSeconds < 10)
                _intervalSeconds = 30;

            _currentHour = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
            _currentHourFilePath = GetHourFilePath(_currentHour);

            _timer = new Timer(_intervalSeconds * 1000.0);
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
            _timer.Start();

            LogEvent("AZM Server Stats Agent gestartet. Server=" + _serverName + ", Intervall=" + _intervalSeconds + "s");
        }

        protected override void OnStop()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
            LogEvent("AZM Server Stats Agent beendet.");
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                DateTime thisHour = now.Date.AddHours(now.Hour);

                if (thisHour > _currentHour)
                {
                    FlushHourToHistory();
                    _currentHour = thisHour;
                    _currentHourFilePath = GetHourFilePath(_currentHour);
                }

                SampleRecord sample = CollectSample(now);
                if (sample == null)
                    return;

                AppendSampleToFile(sample);
                UpdateCurrentInSql(sample);
            }
            catch (Exception ex)
            {
                LogEvent("Fehler beim Sammeln/Schreiben: " + ex.Message);
            }
        }

        private string GetHourFilePath(DateTime hourUtc)
        {
            string name = string.Format(CultureInfo.InvariantCulture, "samples_{0:yyyy-MM-dd}_{1:D2}.jsonl", hourUtc.Date, hourUtc.Hour);
            return Path.Combine(_samplesDirectory, name);
        }

        private SampleRecord CollectSample(DateTime timestamp)
        {
            decimal? cpu = GetCpuPercent();
            long? memUsedKb = null;
            long? memTotalKb = null;
            GetMemoryKb(out memUsedKb, out memTotalKb);
            List<DriveInfo> drives = GetDriveStats();

            return new SampleRecord
            {
                T = timestamp.ToString("o", CultureInfo.InvariantCulture),
                Cpu = cpu,
                MemUsed = memUsedKb.HasValue ? (long)(memUsedKb.Value / 1024) : (long?)null,
                MemTotal = memTotalKb.HasValue ? (long)(memTotalKb.Value / 1024) : (long?)null,
                Drives = drives
            };
        }

        private decimal? GetCpuPercent()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT LoadPercentage FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object v = obj["LoadPercentage"];
                        if (v != null && v != DBNull.Value)
                        {
                            uint u = (uint)v;
                            return (decimal)u;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void GetMemoryKb(out long? usedKb, out long? totalKb)
        {
            usedKb = null;
            totalKb = null;
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object tot = obj["TotalVisibleMemorySize"];
                        object free = obj["FreePhysicalMemory"];
                        if (tot != null && free != null)
                        {
                            totalKb = Convert.ToInt64(tot);
                            long freeKb = Convert.ToInt64(free);
                            usedKb = totalKb.Value - freeKb;
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        private List<DriveInfo> GetDriveStats()
        {
            var list = new List<DriveInfo>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object id = obj["DeviceID"];
                        object size = obj["Size"];
                        object free = obj["FreeSpace"];
                        if (id != null && size != null && free != null)
                        {
                            long totalBytes = Convert.ToInt64(size);
                            long freeBytes = Convert.ToInt64(free);
                            list.Add(new DriveInfo
                            {
                                Drive = id.ToString().Trim(),
                                TotalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2),
                                FreeGB = Math.Round(freeBytes / (1024.0 * 1024.0 * 1024.0), 2)
                            });
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private void AppendSampleToFile(SampleRecord sample)
        {
            string line = JsonConvert.SerializeObject(sample) + Environment.NewLine;
            try
            {
                File.AppendAllText(_currentHourFilePath, line, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                LogEvent("Datei schreiben fehlgeschlagen: " + ex.Message);
            }
        }

        private void UpdateCurrentInSql(SampleRecord sample)
        {
            string driveStatsJson = sample.Drives != null && sample.Drives.Count > 0
                ? JsonConvert.SerializeObject(sample.Drives)
                : null;

            string sql = @"
MERGE [dbo].[azm_tool_server_stats_current] AS t
USING (SELECT @ServerName AS ServerName) AS s ON t.ServerName = s.ServerName
WHEN MATCHED THEN
    UPDATE SET LastUpdated=@LastUpdated, CpuPercent=@CpuPercent, MemoryUsedMB=@MemoryUsedMB, MemoryTotalMB=@MemoryTotalMB, DriveStatsJson=@DriveStatsJson
WHEN NOT MATCHED THEN
    INSERT (ServerName, LastUpdated, CpuPercent, MemoryUsedMB, MemoryTotalMB, DriveStatsJson)
    VALUES (@ServerName, @LastUpdated, @CpuPercent, @MemoryUsedMB, @MemoryTotalMB, @DriveStatsJson);";

            DateTime lastUpdated;
            if (!DateTime.TryParse(sample.T, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastUpdated))
                lastUpdated = DateTime.UtcNow;

            try
            {
                using (var conn = new SqlConnection(_connectionString))
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@ServerName", _serverName);
                    cmd.Parameters.AddWithValue("@LastUpdated", lastUpdated);
                    cmd.Parameters.Add("@CpuPercent", SqlDbType.Decimal).Value = (object)sample.Cpu ?? DBNull.Value;
                    cmd.Parameters.Add("@MemoryUsedMB", SqlDbType.BigInt).Value = (object)sample.MemUsed ?? DBNull.Value;
                    cmd.Parameters.Add("@MemoryTotalMB", SqlDbType.BigInt).Value = (object)sample.MemTotal ?? DBNull.Value;
                    cmd.Parameters.Add("@DriveStatsJson", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(driveStatsJson) ? (object)DBNull.Value : driveStatsJson;
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                LogEvent("SQL Current Update fehlgeschlagen: " + ex.Message);
            }
        }

        private void FlushHourToHistory()
        {
            DateTime hourToFlush = _currentHour;
            string filePath = GetHourFilePath(hourToFlush);
            if (!File.Exists(filePath))
                return;

            List<SampleRecord> samples;
            try
            {
                var lines = File.ReadAllLines(filePath, Encoding.UTF8);
                samples = new List<SampleRecord>();
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var s = JsonConvert.DeserializeObject<SampleRecord>(line);
                    if (s != null) samples.Add(s);
                }
            }
            catch (Exception ex)
            {
                LogEvent("Stunden-Datei lesen fehlgeschlagen: " + ex.Message);
                return;
            }

            if (samples.Count == 0)
            {
                try { File.Delete(filePath); } catch { }
                return;
            }

            decimal? cpuAvg = null;
            long? memUsedAvg = null;
            long? memTotal = null;
            var driveAgg = AggregateDrives(samples);
            string rawSamplesJson = JsonConvert.SerializeObject(samples);

            int cpuCount = 0;
            decimal cpuSum = 0;
            int memCount = 0;
            long memSum = 0;
            foreach (var s in samples)
            {
                if (s.Cpu.HasValue) { cpuSum += s.Cpu.Value; cpuCount++; }
                if (s.MemUsed.HasValue) { memSum += s.MemUsed.Value; memCount++; }
                if (s.MemTotal.HasValue && !memTotal.HasValue) memTotal = s.MemTotal.Value;
            }
            if (cpuCount > 0) cpuAvg = Math.Round(cpuSum / cpuCount, 2);
            if (memCount > 0) memUsedAvg = memSum / memCount;

            string driveAggJson = driveAgg != null && driveAgg.Count > 0 ? JsonConvert.SerializeObject(driveAgg) : null;

            string sql = @"
INSERT INTO [dbo].[azm_tool_server_stats_history] (ServerName, HourStart, CpuAvg, MemoryUsedAvgMB, MemoryTotalMB, DriveStatsAggJson, RawSamplesJson)
VALUES (@ServerName, @HourStart, @CpuAvg, @MemoryUsedAvgMB, @MemoryTotalMB, @DriveStatsAggJson, @RawSamplesJson);";

            try
            {
                using (var conn = new SqlConnection(_connectionString))
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@ServerName", _serverName);
                    cmd.Parameters.AddWithValue("@HourStart", hourToFlush);
                    cmd.Parameters.Add("@CpuAvg", SqlDbType.Decimal).Value = (object)cpuAvg ?? DBNull.Value;
                    cmd.Parameters.Add("@MemoryUsedAvgMB", SqlDbType.BigInt).Value = (object)memUsedAvg ?? DBNull.Value;
                    cmd.Parameters.Add("@MemoryTotalMB", SqlDbType.BigInt).Value = (object)memTotal ?? DBNull.Value;
                    cmd.Parameters.Add("@DriveStatsAggJson", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(driveAggJson) ? (object)DBNull.Value : driveAggJson;
                    cmd.Parameters.Add("@RawSamplesJson", SqlDbType.NVarChar, -1).Value = rawSamplesJson;
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                LogEvent("SQL History Insert fehlgeschlagen: " + ex.Message);
            }

            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                LogEvent("Stunden-Datei löschen fehlgeschlagen: " + ex.Message);
            }
        }

        private List<DriveAggregate> AggregateDrives(List<SampleRecord> samples)
        {
            var byDrive = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in samples)
            {
                if (s.Drives == null) continue;
                foreach (var d in s.Drives)
                {
                    if (string.IsNullOrEmpty(d.Drive)) continue;
                    if (!byDrive.ContainsKey(d.Drive))
                        byDrive[d.Drive] = new List<double>();
                    byDrive[d.Drive].Add(d.FreeGB);
                }
            }
            var result = new List<DriveAggregate>();
            foreach (var kv in byDrive)
            {
                if (kv.Value.Count == 0) continue;
                result.Add(new DriveAggregate
                {
                    Drive = kv.Key,
                    MinFreeGB = Math.Round(kv.Value.Min(), 2),
                    AvgFreeGB = Math.Round(kv.Value.Average(), 2)
                });
            }
            return result;
        }

        private static void LogEvent(string message)
        {
            try
            {
                EventLog.WriteEntry("AzmServerStatsAgent", message, EventLogEntryType.Information);
            }
            catch { }
        }

        private class SampleRecord
        {
            [JsonProperty("t")]
            public string T { get; set; }
            [JsonProperty("cpu")]
            public decimal? Cpu { get; set; }
            [JsonProperty("memUsed")]
            public long? MemUsed { get; set; }
            [JsonProperty("memTotal")]
            public long? MemTotal { get; set; }
            [JsonProperty("drives")]
            public List<DriveInfo> Drives { get; set; }
        }

        private class DriveInfo
        {
            [JsonProperty("Drive")]
            public string Drive { get; set; }
            [JsonProperty("TotalGB")]
            public double TotalGB { get; set; }
            [JsonProperty("FreeGB")]
            public double FreeGB { get; set; }
        }

        private class DriveAggregate
        {
            [JsonProperty("Drive")]
            public string Drive { get; set; }
            [JsonProperty("MinFreeGB")]
            public double MinFreeGB { get; set; }
            [JsonProperty("AvgFreeGB")]
            public double AvgFreeGB { get; set; }
        }
    }
}
