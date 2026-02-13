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
using System.Net;
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
        private string _rosterStatusUri;
        private string _rosterStatusSecret;
        private bool _rosterEnabled;
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

            var connStringSetting = ConfigurationManager.ConnectionStrings["StatsDb"];
            _connectionString = connStringSetting != null ? connStringSetting.ConnectionString : null;
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

            _rosterStatusUri = ConfigurationManager.AppSettings["RosterStatusUri"];
            _rosterStatusSecret = ConfigurationManager.AppSettings["RosterStatusSecret"];
            _rosterEnabled = !string.IsNullOrWhiteSpace(_rosterStatusUri) && !string.IsNullOrWhiteSpace(_rosterStatusSecret);

            _currentHour = DateTime.Now.Date.AddHours(DateTime.Now.Hour);
            _currentHourFilePath = GetHourFilePath(_currentHour);

            _timer = new Timer(_intervalSeconds * 1000.0);
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
            _timer.Start();

            LogEvent("AZM Server Stats Agent gestartet. Server=" + _serverName + ", Intervall=" + _intervalSeconds + "s, RosterEnabled=" + _rosterEnabled);
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
                DateTime now = DateTime.Now;
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

                List<RosterSessionSnapshot> rosterSessions = null;
                if (_rosterEnabled)
                    rosterSessions = CollectRosterSessions();

                AppendSampleToFile(sample);
                UpdateCurrentInSql(sample, rosterSessions);
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
            decimal? wmi = GetCpuPercentWmi();
            if (wmi.HasValue)
                return wmi;
            return GetCpuPercentPerfCounter();
        }

        private decimal? GetCpuPercentWmi()
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
                            if (u > 100) u = 100;
                            return (decimal)u;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private decimal? GetCpuPercentPerfCounter()
        {
            try
            {
                using (var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total"))
                {
                    pc.NextValue();
                    System.Threading.Thread.Sleep(500);
                    float v = pc.NextValue();
                    if (v >= 0 && v <= 100)
                        return Math.Round((decimal)v, 2);
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

        private List<RosterSessionSnapshot> CollectRosterSessions()
        {
            try
            {
                string completeUri = BuildRosterUri(_rosterStatusUri, _rosterStatusSecret);
                if (string.IsNullOrWhiteSpace(completeUri))
                    return new List<RosterSessionSnapshot>();

                var request = (HttpWebRequest)WebRequest.Create(completeUri);
                request.Method = "GET";
                request.Accept = "application/json";
                request.ContentType = "application/json";
                request.Headers["PlanoSource"] = "AzmServerStatsAgent";
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string content = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(content))
                        return new List<RosterSessionSnapshot>();

                    var root = JsonConvert.DeserializeObject<RosterStatusResponse>(content);
                    if (root == null || root.ActiveSessions == null || root.ActiveSessions.Count == 0)
                        return new List<RosterSessionSnapshot>();

                    var list = new List<RosterSessionSnapshot>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in root.ActiveSessions)
                    {
                        string user = s != null && s.UserName != null ? s.UserName.Trim() : "";
                        string emp = s != null && s.EmployeeNumber != null ? s.EmployeeNumber.Trim() : "";
                        if (string.IsNullOrEmpty(user)) continue;
                        string key = (user + "|" + emp).ToUpperInvariant();
                        if (seen.Contains(key)) continue;
                        seen.Add(key);
                        list.Add(new RosterSessionSnapshot { UserName = user, EmployeeNumber = emp });
                    }
                    return list;
                }
            }
            catch (Exception ex)
            {
                LogEvent("Roster API Aufruf fehlgeschlagen: " + ex.Message);
                return new List<RosterSessionSnapshot>();
            }
        }

        private static string BuildRosterUri(string baseUri, string secret)
        {
            if (string.IsNullOrWhiteSpace(baseUri) || string.IsNullOrWhiteSpace(secret))
                return "";
            string encoded = Uri.EscapeDataString(secret);
            return baseUri + encoded;
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

        /// <summary>Ring-Puffer für die letzten 30 Min Samples (max 60 bei 30s-Intervall).</summary>
        private readonly List<GraphSample> _recentSamples = new List<GraphSample>();
        private static readonly int RecentMinutes = 30;

        private void UpdateCurrentInSql(SampleRecord sample, List<RosterSessionSnapshot> rosterSessions)
        {
            string driveStatsJson = sample.Drives != null && sample.Drives.Count > 0
                ? JsonConvert.SerializeObject(sample.Drives)
                : null;

            DateTime lastUpdated;
            if (!DateTime.TryParse(sample.T, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastUpdated))
                lastUpdated = DateTime.Now;

            int? sessionsCount = _rosterEnabled ? (int?)((rosterSessions != null) ? rosterSessions.Count : 0) : (int?)null;

            /* --- Graph-Sample bauen und Ringpuffer pflegen --- */
            var gs = new GraphSample { T = lastUpdated.ToString("yyyy-MM-dd\\THH:mm:ss", CultureInfo.InvariantCulture) };
            gs.Cpu = sample.Cpu.HasValue ? Math.Round(sample.Cpu.Value, 1) : (decimal?)null;
            if (sample.MemUsed.HasValue && sample.MemTotal.HasValue && sample.MemTotal.Value > 0)
                gs.Ram = Math.Round((decimal)sample.MemUsed.Value * 100 / sample.MemTotal.Value, 1);
            gs.Sessions = sessionsCount;
            if (sample.Drives != null && sample.Drives.Count > 0)
            {
                gs.Drives = new List<GraphDrive>();
                foreach (var d in sample.Drives)
                {
                    decimal pct = 0;
                    if (d.TotalGB > 0)
                        pct = Math.Round((decimal)((d.TotalGB - d.FreeGB) * 100.0 / d.TotalGB), 1);
                    if (pct < 0) pct = 0;
                    if (pct > 100) pct = 100;
                    gs.Drives.Add(new GraphDrive { D = d.Drive, Pct = pct });
                }
            }
            _recentSamples.Add(gs);

            /* Einträge älter als 30 Min entfernen */
            DateTime cutoff = lastUpdated.AddMinutes(-RecentMinutes);
            _recentSamples.RemoveAll(delegate(GraphSample s)
            {
                DateTime dt;
                if (DateTime.TryParse(s.T, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                    return dt < cutoff;
                return false;
            });

            string recentSamplesJson = JsonConvert.SerializeObject(_recentSamples);

            /* --- SQL MERGE mit RecentSamplesJson + SessionsCount --- */
            string sql = @"
MERGE [dbo].[azm_tool_server_stats_current] AS t
USING (SELECT @ServerName AS ServerName) AS s ON t.ServerName = s.ServerName
WHEN MATCHED THEN
    UPDATE SET LastUpdated=@LastUpdated, CpuPercent=@CpuPercent, MemoryUsedMB=@MemoryUsedMB, MemoryTotalMB=@MemoryTotalMB, DriveStatsJson=@DriveStatsJson, RecentSamplesJson=@RecentSamplesJson, SessionsCount=@SessionsCount
WHEN NOT MATCHED THEN
    INSERT (ServerName, LastUpdated, CpuPercent, MemoryUsedMB, MemoryTotalMB, DriveStatsJson, RecentSamplesJson, SessionsCount)
    VALUES (@ServerName, @LastUpdated, @CpuPercent, @MemoryUsedMB, @MemoryTotalMB, @DriveStatsJson, @RecentSamplesJson, @SessionsCount);";

            try
            {
                using (var conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        using (var cmd = new SqlCommand(sql, conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@ServerName", _serverName);
                            cmd.Parameters.AddWithValue("@LastUpdated", lastUpdated);
                            cmd.Parameters.Add("@CpuPercent", SqlDbType.Decimal).Value = (object)sample.Cpu ?? DBNull.Value;
                            cmd.Parameters.Add("@MemoryUsedMB", SqlDbType.BigInt).Value = (object)sample.MemUsed ?? DBNull.Value;
                            cmd.Parameters.Add("@MemoryTotalMB", SqlDbType.BigInt).Value = (object)sample.MemTotal ?? DBNull.Value;
                            cmd.Parameters.Add("@DriveStatsJson", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(driveStatsJson) ? (object)DBNull.Value : driveStatsJson;
                            cmd.Parameters.Add("@RecentSamplesJson", SqlDbType.NVarChar, -1).Value = recentSamplesJson;
                            cmd.Parameters.Add("@SessionsCount", SqlDbType.Int).Value = (object)sessionsCount ?? DBNull.Value;
                            cmd.ExecuteNonQuery();
                        }

                        if (_rosterEnabled)
                            SyncRosterSessions(conn, tx, lastUpdated, rosterSessions ?? new List<RosterSessionSnapshot>());

                        tx.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                string full = ex.Message;
                if (ex.InnerException != null)
                    full = full + " | " + ex.InnerException.Message;
                LogEvent("SQL Current Update fehlgeschlagen: " + full);
            }
        }

        private void SyncRosterSessions(SqlConnection conn, SqlTransaction tx, DateTime snapshotTime, List<RosterSessionSnapshot> currentSessions)
        {
            var open = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            const string selectOpenSql = @"SELECT Id, UserName, EmployeeNumber FROM [dbo].[azm_tool_roster_sessions] WHERE ServerName=@ServerName AND SessionEnd IS NULL";
            using (var cmd = new SqlCommand(selectOpenSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@ServerName", _serverName);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long id = reader.GetInt64(0);
                        string user = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        string emp = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        string key = BuildSessionKey(user, emp);
                        if (!open.ContainsKey(key))
                            open[key] = id;
                    }
                }
            }

            var current = new Dictionary<string, RosterSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in currentSessions)
            {
                string key = BuildSessionKey(s.UserName, s.EmployeeNumber);
                if (!current.ContainsKey(key))
                    current[key] = s;
            }

            const string insertSql = @"INSERT INTO [dbo].[azm_tool_roster_sessions] (ServerName, SnapshotTime, UserName, EmployeeNumber, SessionStart, SessionEnd) VALUES (@ServerName, @SnapshotTime, @UserName, @EmployeeNumber, @SessionStart, NULL)";
            foreach (var kv in current)
            {
                if (open.ContainsKey(kv.Key)) continue;
                using (var cmd = new SqlCommand(insertSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@ServerName", _serverName);
                    cmd.Parameters.AddWithValue("@SnapshotTime", snapshotTime);
                    cmd.Parameters.AddWithValue("@UserName", kv.Value.UserName ?? "");
                    cmd.Parameters.AddWithValue("@EmployeeNumber", string.IsNullOrEmpty(kv.Value.EmployeeNumber) ? (object)DBNull.Value : kv.Value.EmployeeNumber);
                    cmd.Parameters.AddWithValue("@SessionStart", snapshotTime);
                    cmd.ExecuteNonQuery();
                }
            }

            const string updateEndSql = @"UPDATE [dbo].[azm_tool_roster_sessions] SET SessionEnd=@SessionEnd WHERE Id=@Id AND SessionEnd IS NULL";
            foreach (var kv in open)
            {
                if (current.ContainsKey(kv.Key)) continue;
                using (var cmd = new SqlCommand(updateEndSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@SessionEnd", snapshotTime);
                    cmd.Parameters.AddWithValue("@Id", kv.Value);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static string BuildSessionKey(string userName, string employeeNumber)
        {
            string user = userName == null ? "" : userName.Trim();
            string emp = employeeNumber == null ? "" : employeeNumber.Trim();
            return (user + "|" + emp).ToUpperInvariant();
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
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AzmServerStatsAgent.log");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine;
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
            catch { }
        }

        private class RosterStatusResponse
        {
            [JsonProperty("activeSessions")]
            public List<RosterSessionApiItem> ActiveSessions { get; set; }
        }

        private class RosterSessionApiItem
        {
            [JsonProperty("userName")]
            public string UserName { get; set; }
            [JsonProperty("employeeNumber")]
            public string EmployeeNumber { get; set; }
        }

        private class RosterSessionSnapshot
        {
            public string UserName { get; set; }
            public string EmployeeNumber { get; set; }
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

        /// <summary>Graph-Sample für RecentSamplesJson (Frontend Chart.js).</summary>
        private class GraphSample
        {
            [JsonProperty("t")]
            public string T { get; set; }
            [JsonProperty("cpu")]
            public decimal? Cpu { get; set; }
            [JsonProperty("ram")]
            public decimal? Ram { get; set; }
            [JsonProperty("sessions")]
            public int? Sessions { get; set; }
            [JsonProperty("drives")]
            public List<GraphDrive> Drives { get; set; }
        }

        private class GraphDrive
        {
            [JsonProperty("d")]
            public string D { get; set; }
            [JsonProperty("pct")]
            public decimal Pct { get; set; }
        }
    }
}
