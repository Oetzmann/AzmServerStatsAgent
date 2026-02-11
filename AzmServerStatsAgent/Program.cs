using System;
using System.ServiceProcess;

namespace AzmServerStatsAgent
{
    static class Program
    {
        static void Main()
        {
            ServiceBase[] servicesToRun = new ServiceBase[]
            {
                new ServerStatsService()
            };
            ServiceBase.Run(servicesToRun);
        }
    }
}
