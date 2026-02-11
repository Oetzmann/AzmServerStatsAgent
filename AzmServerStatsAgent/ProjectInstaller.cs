using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;

namespace AzmServerStatsAgent
{
    [RunInstaller(true)]
    public class ProjectInstaller : Installer
    {
        private ServiceProcessInstaller processInstaller;
        private ServiceInstaller serviceInstaller;

        public ProjectInstaller()
        {
            processInstaller = new ServiceProcessInstaller();
            processInstaller.Account = ServiceAccount.LocalSystem;

            serviceInstaller = new ServiceInstaller();
            serviceInstaller.ServiceName = "AzmServerStatsAgent";
            serviceInstaller.DisplayName = "AZM Server Stats Agent";
            serviceInstaller.Description = "Sammelt CPU, RAM und Laufwerksdaten, schreibt alle 30 s in lokale Datei und SQL Current; stündlich Aggregation in History.";
            serviceInstaller.StartType = ServiceStartMode.Automatic;

            Installers.Add(processInstaller);
            Installers.Add(serviceInstaller);
        }
    }
}
