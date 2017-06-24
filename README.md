# NetworkSharingIPv6
Delegating the received /64 subnet further down to your network. To be able to use IoT in the conventional environment without additional requests from the provider to allocate /48 subnet

# Prerequisities
1. Make sure your provider supports IPv6
1. IPv6 enabed on your machine.
1. Make sure you have IPv6 address with /64 subnet issued to your machine
1. Configure forwarding on both the interface you are re willing to "share from" AND the interfaces you are willing to "share to".
1. Configure advertisement on the interfaces you are willing to "share to" (including default route)
1. Configure, install and run the service

# Compatibility
Tested on Windows 10 x64. You are welcome to build and test on your machine. Please report any bugs found.

# How to check the connectivity
1. Ping ::1
1. Ping any link local address (by name, by address)
1. Ping any global address

# DNS name resolution
If you can ping any IPv6 global address - you are set.

# Other projects to look at
Please refer to another project llmnr-multihost if you need IPv6 for your virtual machine setup and development purposes.
Handy to have multiple names bound to a single machine without any additional configuration, right?
