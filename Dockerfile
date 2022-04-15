FROM mcr.microsoft.com/dotnet/runtime:6.0
LABEL product="Technitium DNS Server"
LABEL vendor="Technitium"
LABEL email="support@technitium.com"
LABEL project_url="https://technitium.com/dns/"
LABEL github_url="https://github.com/TechnitiumSoftware/DnsServer"

WORKDIR /etc/dns/

COPY ./DnsServerApp/bin/Release/publish/ .

EXPOSE 5380 53/udp 53/tcp 67/udp 853 80 443 8053

VOLUME ["/etc/dns/config"]

CMD ["/usr/bin/dotnet", "/etc/dns/DnsServerApp.dll"]
