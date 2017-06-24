using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace NetworkSharing
{
    static class Program
    {
        public static readonly string MyName = "NetworkSharingIPv6";
        public static readonly string MyProgramDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetworkSharingIPv6");
        public static readonly string MyProgramDir_Log = System.IO.Path.Combine(MyProgramDir, "activity.log");

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main()
        {
            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new Service()
            };
            ServiceBase.Run(ServicesToRun);
        }
    }
}
