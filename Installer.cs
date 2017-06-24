using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;

namespace NetworkSharing
{
    [RunInstaller(true)]
    public class ThisServiceInstaller : Installer
    {
        public ThisServiceInstaller()
        {
            using (ServiceProcessInstaller procInstaller = new ServiceProcessInstaller())
            {
                procInstaller.Account = ServiceAccount.LocalSystem;
                using (ServiceInstaller installer = new ServiceInstaller())
                {
                    installer.StartType = ServiceStartMode.Automatic;
                    installer.DelayedAutoStart = true;
                    installer.ServiceName = "NetworkSharingIPv6";
                    installer.DisplayName = "Network Sharing IPv6 DHCPv6";
                    installer.Description = "Performs automatic DHCPv6 invocation on the listed interfaces to share IPv6 connectivity with them.";

                    this.Installers.Add(procInstaller);
                    this.Installers.Add(installer);
                }
            }
        }
    }
}
