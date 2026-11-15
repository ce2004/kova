Kova Relay on Linux

Kova Relay pairs Kova hosts with the PCs that control them, so a PC behind a router can be reached without opening a port on it. On Linux it is one program, kovarelay, with no window. It is the same relay as KovaRelay.exe on Windows, built from the same code: the same protocol, the same limits, the same log. A Windows relay and a Linux relay are interchangeable, and Kova cannot tell them apart.

It runs on 64-bit Linux with the usual GNU C library, such as Debian 12 or 13 and Ubuntu 22.04 or later, on Intel or AMD processors (x64) and on ARM (arm64). Nothing needs installing first: .NET is inside the program. It uses the system's OpenSSL library, libssl3, which Debian and Ubuntu already have.


Building it

On any PC with the .NET 8 SDK, Windows or Linux, from the folder that holds kova.csproj:

    dotnet publish relay/linux/kovarelay.csproj -c Release -r linux-x64 -o out/linux-x64
    dotnet publish relay/linux/kovarelay.csproj -c Release -r linux-arm64 -o out/linux-arm64

Each makes one file, kovarelay, about 12 MB. That file is all the server needs. Its version is the Windows relay's version.


Trying it by hand

Copy kovarelay to the server, make it runnable, and start it:

    chmod +x kovarelay
    ./kovarelay

It prints what it does and runs until you press Control C. Other PCs can use it as soon as its port is open (see Opening the port below).

    ./kovarelay --help        every option, in plain sentences
    ./kovarelay --version     the version
    ./kovarelay --selftest    tests the relay on this computer, then prints a line that starts with ok or FAIL
    ./kovarelay --port 6968   uses another port


Installing it as a service

These commands need root, so start each with sudo or run them as root.

1. Put the program in place:

    install -m 755 kovarelay /usr/local/bin/kovarelay

2. Put the service file in place. It is kovarelay.service, beside this file:

    install -m 644 kovarelay.service /etc/systemd/system/kovarelay.service

3. Only if you want a port other than 47200, write the settings file:

    mkdir -p /etc/kovarelay
    echo '{ "Port": 6968 }' > /etc/kovarelay/settings.json

4. Start it now and at every boot:

    systemctl daemon-reload
    systemctl enable --now kovarelay

5. Check that it runs:

    systemctl status kovarelay
    journalctl -u kovarelay

The service runs as a user of its own with no rights, never as root, and can write nowhere but its own folder, /var/lib/kovarelay. systemd starts it again if it ever stops by itself.


Opening the port

The relay does not touch the firewall. Open its port, 47200 unless you changed it, for both TCP and UDP. TCP carries keys, files and the screen. UDP carries sound.

With ufw:

    ufw allow 47200/tcp
    ufw allow 47200/udp

With nftables, add to your input chain:

    tcp dport 47200 accept
    udp dport 47200 accept

Many cloud providers have a firewall of their own in their control panel. Open the port there as well, for TCP and UDP.

The relay listens on IPv4 and IPv6.


Settings

The port, most specific first:
- the option --port
- the environment variable KOVA_RELAY_PORT
- the file /etc/kovarelay/settings.json, which holds the port like this: { "Port": 47200 }
- otherwise 47200

This is the same settings.json the Windows relay keeps in ProgramData, KovaRelay, so the file can be copied from one to the other. After changing it, restart the relay: systemctl restart kovarelay

If the settings file is there but cannot be understood, the relay says what is wrong and does not start, rather than quietly using another port.

The option --settings FILE reads another file instead.


The log

Everything the relay logs is printed, so under systemd it is in the journal:

    journalctl -u kovarelay -f

It is also written to relay-log.txt in the relay's data folder, kept under 1 MB, with the one before it kept as relay-log.old.txt. The data folder, most specific first:
- the option --data
- the environment variable KOVA_RELAY_DATA
- the folder systemd made for the service, /var/lib/kovarelay
- /var/lib/kovarelay when the relay is started by hand by a user who may write there, such as root
- otherwise .local/share/kovarelay in your home folder

With the service file here, systemd keeps the folder in /var/lib/private/kovarelay and /var/lib/kovarelay leads to it. Only root can look inside.

The log says which addresses connected and when. It never contains passwords, the codes made from them, or anything that passes between the PCs.


Stopping and updating

    systemctl stop kovarelay

The relay closes every connection and ends. Kova on each PC reconnects by itself when the relay is back.

To update, build the new kovarelay, copy it to the server, then:

    install -m 755 kovarelay /usr/local/bin/kovarelay
    systemctl restart kovarelay

The Linux relay does not update itself.


Removing it

    systemctl disable --now kovarelay
    rm /etc/systemd/system/kovarelay.service /usr/local/bin/kovarelay
    rm -r /etc/kovarelay
    systemctl daemon-reload


What the Linux relay does not have

- No window and no notification area icon. Use systemctl and journalctl.
- No port editor and no firewall handling. Open the port yourself, as above.
- No list of the hosts connected now. The log says when each host and each controlling PC connects and disconnects.
- Changing the port means editing the settings file and restarting. Nothing asks for permission, because only root can write in /etc.
