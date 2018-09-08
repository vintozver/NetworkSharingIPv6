using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.ServiceProcess;
using System.Net.NetworkInformation;

namespace NetworkSharing
{
    public class ServedInterface : IEquatable<ServedInterface>
    {
        /// <summary>
        /// UUID/GUID from Windows, {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Common name (from the Network Manager)
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Index (routing tables, internals). For PowerShell operations
        /// </summary>
        public int Index { get; }

        /// <summary>
        /// NetworkId (the part of the address which identifies the network)
        /// </summary>
        public UInt16 NetworkId { get; }

        public ServedInterface(string Id, string Name, int Index, UInt16 NetworkId)
        {
            Debug.Assert(!string.IsNullOrEmpty(Id));
            this.Id = Id;
            Debug.Assert(!string.IsNullOrEmpty(Name));
            this.Name = Name;
            Debug.Assert(Index != -1);
            this.Index = Index;
            this.NetworkId = NetworkId;
        }

        public static ServedInterface CreateFromId(string Id, UInt16 NetworkId)
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.Id.Equals(Id))
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up)
                    {
                        throw new NetworkInformationException();
                    }
                    var IpProperties = adapter.GetIPProperties();
                    if (IpProperties == null)
                    {
                        throw new NetworkInformationException();
                    }
                    var IpV6Properties = IpProperties.GetIPv6Properties();
                    if (IpV6Properties == null)
                    {
                        throw new NetworkInformationException();
                    }
                    return new ServedInterface(Id, adapter.Name, IpV6Properties.Index, NetworkId);
                }
            }
            throw new ArgumentException("Network adapter with the specified Id was not found");
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode() ^ Name.GetHashCode() ^ Index.GetHashCode() ^ NetworkId.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            // If parameter cannot be casted to ServedInterface - return false
            ServedInterface p = obj as ServedInterface;
            if ((object)p == null)
            {
                return false;
            }

            // Return true if the fields match:
            return this.Equals(p);
        }

        public bool Equals(ServedInterface other)
        {
            if (other == null) return false;
            return this.Id == other.Id && this.Name == other.Name && this.Index == other.Index && this.NetworkId == other.NetworkId;
        }

        public static bool operator ==(ServedInterface a, ServedInterface b)
        {
            // If both are null, or both are same instance, return true.
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            // If one is null, but not both, return false.
            if ((object)a == null || (object)b == null)
            {
                return false;
            }

            // Perform the actual comparison
            return a.Equals(b);
        }
        public static bool operator !=(ServedInterface a, ServedInterface b)
        {
            return !(a == b);
        }
    };

    public class ServiceConfig : IEquatable<ServiceConfig>
    {
        public HashSet<ServedInterface> ServedInterfaceList = new HashSet<ServedInterface>();
        public List<string> WanInterfaceList = new List<string>();

        public bool Equals(ServiceConfig other)
        {
            if (other == null) return false;
            return this.ServedInterfaceList.SetEquals(other.ServedInterfaceList) && this.WanInterfaceList.SequenceEqual(other.WanInterfaceList);
        }
    }


    public partial class Service : ServiceBase
    {
        private ProgramConfig ProgramConfigInstance;
        private ServiceImpl ServiceImplInstance;
        private NetworkAddressChangedEventHandler AddressChangeHandler;

        private void ReloadConfiguration()
        {
            Debug.Assert(ServiceImplInstance != null);
            var ServiceConfigInstance = new ServiceConfig();
            foreach (var ProgramInterfaceDefinition in ProgramConfigInstance.LanInterfaceList)
            {
                ServedInterface ServedInterfaceInstance = null;
                try
                {
                    ServedInterfaceInstance = ServedInterface.CreateFromId(ProgramInterfaceDefinition.Id, ProgramInterfaceDefinition.NetworkId);
                }
                catch (NetworkInformationException)
                {
                }
                catch (ArgumentException)
                {
                }
                if (ServedInterfaceInstance != null)
                {
                    ServiceConfigInstance.ServedInterfaceList.Add(ServedInterfaceInstance);
                }

            }
            ServiceConfigInstance.WanInterfaceList = ProgramConfigInstance.WanInterfaceList;
            ServiceImplInstance.RefreshAddresses(ServiceConfigInstance);
        }

        public void AddressChangedCallback(object sender, EventArgs e)
        {
            ReloadConfiguration();
        }

        public Service(ProgramConfig Config)
        {
            this.ProgramConfigInstance = Config;
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            Program.Log("Starting the service");
            Debug.Assert(ServiceImplInstance == null);
            ServiceImplInstance = new ServiceImpl();
            AddressChangeHandler = new NetworkAddressChangedEventHandler(AddressChangedCallback);
            NetworkChange.NetworkAddressChanged += AddressChangeHandler;
            Program.Log("Performing initial config");
            ReloadConfiguration();
            Program.Log("Service is started");
        }

        protected override void OnStop()
        {
            Program.Log("Service is stopping ...");
            Debug.Assert(ServiceImplInstance != null);
            NetworkChange.NetworkAddressChanged -= AddressChangeHandler;
            ServiceImplInstance.Dispose();
            ServiceImplInstance = null;
            Program.Log("Service is stopped");
        }
    }
}
