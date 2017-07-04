using System;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Management;

namespace NetworkSharing
{
    public class ServiceImpl : IDisposable
    {
        private ServiceConfig Configuration = new ServiceConfig();
        private string UsedInterfaceId = string.Empty;
        private string UsedInterfaceName = string.Empty;
        private IPAddress UsedIpAddress = null;
        private Process DhcpServiceProcess = null;

        private IPAddress GetServedSubnet(UInt16 NetworkId)
        {
            Debug.Assert(!string.IsNullOrEmpty(UsedInterfaceId));
            Debug.Assert(!string.IsNullOrEmpty(UsedInterfaceName));
            Debug.Assert(UsedIpAddress != null);
            Debug.Assert(UsedIpAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

            // Full length: 128 bit | 16 bytes
            // Prefix length: 64 bit, 8 bytes
            // Address is represented in the network order (as is)
            byte[] UsedIpAddressBytes = UsedIpAddress.GetAddressBytes();
            Debug.Assert(UsedIpAddressBytes.Length == 16);
            // We will slice this /64 subnet as by /112 subnets as follows
            // XXXX:XXXX:XXXX:XXXX:FFFF:FFFF:NNNN:****:
            // X - assigned by provider
            // N - network id
            // * - host id (will be zero in the subnet)
            int UsedIpAddressBytesIndex = 8;
            while (UsedIpAddressBytesIndex < 12) UsedIpAddressBytes[UsedIpAddressBytesIndex++] = 0xff;
            byte[] NetworkIdBytes = BitConverter.GetBytes(NetworkId);
            if (BitConverter.IsLittleEndian) Array.Reverse(NetworkIdBytes);
            Debug.Assert(NetworkIdBytes.Length == 2);
            UsedIpAddressBytes[UsedIpAddressBytesIndex++] = NetworkIdBytes[0];
            UsedIpAddressBytes[UsedIpAddressBytesIndex++] = NetworkIdBytes[1];
            while (UsedIpAddressBytesIndex < 16) UsedIpAddressBytes[UsedIpAddressBytesIndex++] = 0x0;
            return new IPAddress(UsedIpAddressBytes);
        }

        private string RenderDibblerServerConfig()
        {
            StringBuilder ConfigBuilder = new StringBuilder();
            ConfigBuilder.Append(@"
log-level 7
log-mode short

");
            foreach (var ServedInterface in Configuration.ServedInterfaceList)
            {
                ConfigBuilder.AppendFormat(@"
iface ""{1}"" # {0}
{{
    T1 60  # renew timout
    T2 120  # emergency rebind timeout
    preferred-lifetime 120
    valid-lifetime 300

    class
    {{
        pool {2}/112
    }}
    # Google DNS64
    option dns-server 2001:4860:4860::6464, 2001:4860:4860::64
    option domain local
}}
", ServedInterface.Id, ServedInterface.Name.Replace("\"", "\\\""), GetServedSubnet(ServedInterface.NetworkId).ToString());
            }
            return ConfigBuilder.ToString();
        }

        private void NewDibblerServerProcess()
        {
            Debug.Assert(DhcpServiceProcess == null);
            // Generate the DHCPv6 server config
            string DaemonDirectory = Path.Combine(Program.MyProgramDir, "dibbler_server");
            Directory.CreateDirectory(DaemonDirectory);  // Won't fail if the directory already exists, or will create a new one
            string DibblerServerConfigPath = Path.Combine(DaemonDirectory, "server.conf");
            using (FileStream ConfigFile = File.OpenWrite(DibblerServerConfigPath))
            {
                Byte[] content = new UTF8Encoding(true).GetBytes(RenderDibblerServerConfig());
                ConfigFile.Write(content, 0, content.Length);
                ConfigFile.SetLength(content.Length);
            }
            // Run the DHCPv6 process
            DhcpServiceProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Program.MyProgramDir, "dibbler-server.exe"),
                    Arguments = "run",
                    WorkingDirectory = DaemonDirectory
                }
            };
            DhcpServiceProcess.Start();
            // Add routes
            var mgmtRouteCls = new ManagementClass(new ManagementScope("\\\\.\\ROOT\\StandardCimv2"), new ManagementPath("MSFT_NetRoute"), null);
            foreach (var ServedInterface in Configuration.ServedInterfaceList)
            {
                var mgmtRoute_CreateParams = mgmtRouteCls.GetMethodParameters("Create");
                mgmtRoute_CreateParams["DestinationPrefix"] = GetServedSubnet(ServedInterface.NetworkId).ToString() + "/112";
                mgmtRoute_CreateParams["InterfaceIndex"] = Convert.ToUInt32(ServedInterface.Index);
                mgmtRoute_CreateParams["AddressFamily"] = Convert.ToUInt16(23);  // IPv6
                mgmtRoute_CreateParams["NextHop"] = "::";
                mgmtRoute_CreateParams["Publish"] = Convert.ToByte(2);  // Yes
                mgmtRoute_CreateParams["PolicyStore"] = "ActiveStore";
                try
                {
                    var mgmtRoute_CreateResult = mgmtRouteCls.InvokeMethod("Create", mgmtRoute_CreateParams, null);
                }
                catch (ManagementException ex)
                {
                    Program.LogEventLogError(string.Format("Error(s) occured while adding a route. More information: {0}", ex.ToString()));
                }
            }
        }

        private void KillDibblerServerProcess()
        {
            Debug.Assert(DhcpServiceProcess != null);
            // Remove routes
            try
            {
                var mgmtRouteCls = new ManagementClass(new ManagementScope("\\\\.\\ROOT\\StandardCimv2"), new ManagementPath("MSFT_NetRoute"), null);
                foreach (var ServedInterface in Configuration.ServedInterfaceList)
                {
                    foreach (ManagementObject mgmtRoute in mgmtRouteCls.GetInstances())
                    {
                        if (
                            (mgmtRoute["DestinationPrefix"] as string) == (GetServedSubnet(ServedInterface.NetworkId).ToString() + "/112")
                            &&
                            (mgmtRoute["InterfaceIndex"] as UInt32?) == ServedInterface.Index
                            &&
                            (mgmtRoute["AddressFamily"] as UInt16?) == 23  // IPv6
                            &&
                            (mgmtRoute["NextHop"] as string) == "::"
                            &&
                            (mgmtRoute["Publish"] as Byte?) == 2  // Yes
                            &&
                            (mgmtRoute["Store"] as Byte?) == 1  // Active
                        )
                        {
                            try
                            {
                                mgmtRoute.Delete();
                            }
                            catch (ManagementException ex)
                            {
                                Program.LogEventLogError(string.Format("Error(s) occured while removing the route. More information: {0}", ex.ToString()));
                            }
                        }
                    }
                }
            }
            catch (System.Runtime.InteropServices.COMException com_ex)
            {
                unchecked
                {
                    if (com_ex.HResult == (int)0x8007045B)
                    {
                        // System shutdown is in progress. Ignore.
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            // Kill the DHCPv6 process
            try
            {
                DhcpServiceProcess.Kill();
            }
            catch (System.InvalidOperationException)
            {
                Program.LogEventLogWarning("Could not kill dibbler-server (DHCPv6 daemon). This might cause problems. Please stop the service, kill the rest of lingering daemons and restart the service again.");
            }
            finally
            {
                DhcpServiceProcess = null;
            }
        }

        public void RefreshAddresses(ServiceConfig NewConfiguration)
        {
            string NewUsedInterfaceId = string.Empty;
            string NewUsedInterfaceName = string.Empty;
            IPAddress NewUsedIpAddress = null;

            NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface adapter in adapters)
            {
                // Skip non-operational adapters, loopback adapters
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var IpProperties = adapter.GetIPProperties();

                foreach (var IpAddressInfo in IpProperties.UnicastAddresses)
                {
                    var IpAddress = IpAddressInfo.Address;
                    if (IpAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                        IpAddressInfo.PrefixLength == 64 &&
                        !IpAddress.IsIPv6LinkLocal && !IpAddress.IsIPv6Multicast && !IpAddress.IsIPv6SiteLocal && !IpAddress.IsIPv6Teredo
                        )
                    {
                        NewUsedInterfaceId = adapter.Id;
                        NewUsedInterfaceName = adapter.Name;
                        NewUsedIpAddress = IpAddressInfo.Address;
                        Program.Log(String.Format("Discovered interface {0}({1}), address {2}. Will be using this address. I won't start DHCPv6 daemon on this interface, of course.",
                            NewUsedInterfaceId, NewUsedInterfaceName, NewUsedIpAddress.ToString()
                        ));
                        break;  // address has been found
                    }
                }
            }

            if (string.Equals(UsedInterfaceId, NewUsedInterfaceId) && IPAddress.Equals(UsedIpAddress, NewUsedIpAddress) && NewConfiguration.ServedInterfaceList.SetEquals(Configuration.ServedInterfaceList))
            {
                Program.Log("No changes");
            }
            else
            {
                Program.Log("Changes. Killing and restarting DHCPv6 servers");

                if (UsedIpAddress != null)
                {
                    // Kill all DHCPv6 servers
                    KillDibblerServerProcess();
                }

                Configuration = NewConfiguration;

                UsedInterfaceId = NewUsedInterfaceId;
                UsedInterfaceName = NewUsedInterfaceName;
                UsedIpAddress = NewUsedIpAddress;

                if (UsedIpAddress != null)
                {
                    // Start new DHCPv6 servers
                    NewDibblerServerProcess();
                }
            }
        }

        #region IDisposable Support
        private object disposeLock = new object();
        private bool disposedFlag = false;  // To detect redundant calls

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool bothManagedAndNative)
        {
            if (bothManagedAndNative)
            {
                lock (disposeLock)
                {
                    if (!disposedFlag)
                    {
                        if (UsedIpAddress != null)
                        {
                            KillDibblerServerProcess();
                        }
                        UsedIpAddress = null;
                        disposedFlag = true;
                    }
                }
            }
        }

        #endregion

        ~ServiceImpl()
        {
            Dispose(false);
        }
    }
}
