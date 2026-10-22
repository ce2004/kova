Kova

Control another Windows PC with your keyboard, and hear everything it plays. Built for NVDA. Works over Tailscale or straight over the internet; everything is encrypted either way.

Files
- bin\arm64\Kova.exe for ARM64 PCs, bin\x64\Kova.exe for x64 PCs. Each is one self-contained file; no .NET install needed.

On the PC you want to control (the host)
1. Either install Tailscale on both PCs and sign in to the same tailnet, or use the host's internet address.
2. Run Kova. In the File menu, choose Mode, Host, set Password, then choose Start hosting.
3. A cloud PC usually has no sound card. Kova notices and sets up an audio device called Kova: approve the administrator prompt, then press Install Driver in VB-Cable's window. A progress bar shows the rest. You can also use Settings, Set up audio device at any time, and Settings, Remove audio device takes it off again.
4. If Windows Firewall would keep other PCs out, Kova offers to open the port. When you change the port on a PC that hosts, Kova opens the new one and offers to close the old one. Settings, Port editor lists every port Kova has used or has a firewall rule for, marks the one in use, and opens, closes or removes the others. Its Clean up old firewall rules button removes the rules Windows piled up for copies of Kova that are gone or no longer used, and old TailRemote rules. The port in use always stays open.
5. Turn on Settings, Start hosting when Windows starts. It asks for administrator once, then starts hosting at every sign-in as administrator, so keys also reach administrator windows and the port is opened by itself.

On your PC
1. Run Kova. In the File menu, choose Mode, Control another PC.
2. In the File menu, set Address to the host's Tailscale name, 100 address or internet address, and the same Port and Password. Then choose Connect, the first item in the File menu.
3. In the Kova window, press Control Shift Enter to control the remote PC. Every key goes there: Windows, Alt Tab, your NVDA key, everything. The mouse goes there too: moving it, clicking and the wheel all happen on the remote PC, and this PC's pointer stays where it was until you come back.
4. Press Control Shift Enter again to come back to this PC.

The menus
- The window is blank: everything is in the menu bar. Alt, then Enter, is Connect (or Start hosting).
- Press Alt, then Right Arrow to move between File, Clipboard and Settings, and Down Arrow through each menu. Escape goes back.
- On a setting, Enter turns it on or off and says checked or unchecked. The menu stays open, so you can change several in a row. Choices such as Sound quality are submenus: their name says the current choice, and Enter on another one picks it.
- Address, Port and the passwords are menu items that say what they are set to (passwords only say set or not set). Enter opens a small box to change them, with Show password for passwords. If something is missing or wrong when you connect, that box opens by itself and says what is wrong.
- File: Connect or Disconnect (Start or Stop hosting), Switch to PC, Wake a saved PC and connect, Control remote PC, Restart remote PC, Streaming (Enter says it again), Mode, Saved PCs, Address, Port, Password, Listen-only password, Copy this PC's IP address (it shows the addresses), Save this PC, Forget the chosen saved PC and Exit.
- Clipboard: Files (what is moving right now; Enter says it again, and while several files move it opens a line per file), Send the clipboard (Control B), Send files (Control I), Send a folder (Control T), Stop the file transfer, and Pick where received files go.
- About: Check for updates (Control U), View the changelog (Shift F1), Read the guide (F1), Keyboard shortcuts, About Kova, Licences and credits, Open the Kova website, Report a problem on GitHub, and Open the log file. The changelog, guide and the rest open as a list: the arrows go a line at a time, Control Down and Control Up jump between versions or sections, and Control C copies a line.
- Settings: Sound quality, Output device, Capture sound from, Catch up by fast-forwarding, Sounds, Start hosting when Windows starts, Run as a Windows service, Port editor, Set up audio device, Remove audio device and Enable logging.

Features
- Control Shift Enter in the Kova window switches between the remote PC and this one.
- Control Alt End sends Control Alt Delete to the remote PC, when it runs Kova as a service.
- Send the clipboard (Control B) sends what is on this PC's clipboard to the other PC's clipboard: text, or files and folders you copied. Paste it there with Control V. Send files (Control I) sends files you choose to Downloads, Kova on the other PC, never over anything already there. Send a folder and everything in it (Control T) does the same for a whole folder. Each file moving has its own progress bar. Both PCs have the Clipboard menu; nothing is ever sent by itself.
- Clipboard, Pick where received files go chooses the folder files from the other PC are saved in (Downloads, Kova at first). A transfer interrupted by a dropped connection or Disconnect carries on by itself when you connect again; whatever arrived is always kept.
- When hosting, File, Copy this PC's IP address shows the addresses to connect to, and Enter copies the home network one (or Tailscale's).
- Clipboard, Files shows what is being sent or received, how far along, the speed and the time left. Clipboard, Stop the file transfer ends it.
- Settings, Sounds for connecting, clipboard and files: a sound for each event. Default always plays the event's own tune on the piano; Random sound plays anything; or choose one of the instruments (a real Steinway, harp, glockenspiel, marimba, xylophone, pizzicato strings, classic phone and more) or Android's notification sounds, and the key (Alt K). Kova.exe --licence writes where the sounds come from.
- File, Show the remote screen (Control Shift S) is a checkbox. Checked, a window opens with the remote PC's screen. While things move it is smooth video, up to 60 frames a second (H.264, on the remote PC's graphics chip when it has one); once they stop, an exact picture, every pixel as on the remote screen. The title bar always says how many frames a second are coming (0 while nothing moves), so NVDA plus T reads it; while the screen is on, the main window's title says it too. NVDA plus R reads it with Windows' text recognition. It follows the connection by itself, like the sound, and is only taken while the window is open (paused while it is minimized). Escape goes back to the main window and leaves the screen on. Control Shift S, or unchecking it in the File menu, turns it off and closes the window. F5 gets a whole new picture. If the remote PC has more than one screen, the main one shows first; keys 1 to 9 pick a screen and Page Down and Page Up go to the next or previous one, and Kova says which one it is, its size and where it sits ("Screen 2 of 3, 1920 by 1080, right of the main screen."). When Kova runs as the service there, the welcome screen, the lock screen and administrator prompts show too.
- File, Update the remote PC updates Kova on the PC you control to this PC's version (it must be the newest on GitHub). It works for the Kova service too. The remote PC keeps going while it downloads, then you hear it is updating and when it is back.
- Clipboard, Get files from the remote PC (Control G) lists the remote PC's usual folders and drives. Enter opens a folder or checks a file, Space checks files and folders, Backspace goes up, and checks stay as you move between folders. Nothing comes until Get the checked files and folders (Alt G), which brings them all at once to your received files folder. Each then says getting, then got, and Kova says when they have arrived.
- Clipboard, History of transfers lists every send and receive that ended, with the time, until Clear the history.
- Settings, Ride out Wi-Fi scans (on at first): when the sound keeps stalling, as when a Wi-Fi card scans for networks, it holds enough sound to play through the stalls, for a little more delay until they stop.
- File, Remote PC info (Control Shift I) shows the remote PC's name, Windows version, who is signed in, how long it has been on, processor, memory, battery, free disk space, Kova version and who is connected, one line each.
- Several PCs at once: Control 1 to 9 (or File, Switch to PC) brings saved PC 1 to 9 to the front, and the one you were on stays connected in the background. You only hear and control the one in front; the others are silent and use almost nothing, and their file transfers carry on. Switching back is instant. Switch to PC says which PCs are in front, in the background or not connected, and has Disconnect the PCs in the background. Disconnect only disconnects the one in front.
- File, Wake a saved PC and connect: wakes a sleeping or switched-off PC with Wake-on-LAN, then connects as soon as it answers. Kova learns how to wake a saved PC the first time you connect to it while it is on. The signal only reaches the PC's own network, so it is also sent through every PC you are connected to: away from home, connect to any other PC at home first. The PC's network card has to allow Wake-on-LAN (in Device Manager, and on some PCs in the BIOS).
- File, Measure this PC's internet speed, or Measure the remote PC's internet speed: about 20 seconds later a window shows download and upload speed, ping and jitter, the Cloudflare data centre it tested against, the internet address and how much data it used (at most about 1.5 gigabytes). The sound may break up while it runs.
- Settings, Back up settings to a file saves every setting and saved PC, with their passwords, in one file locked with a password you choose. Settings, Restore settings from a backup puts them on this PC (disconnect first).
- Settings, Announce the remote PC's health (on unless you turn it off): says when the remote PC goes onto its battery or is plugged in, when the battery is low or very low, when its Windows drive is nearly full, when its processor has been busy for a whole minute, and when it is nearly out of memory. Each is said once, and again only after it got better. Problems that are already there are said when you connect.
- Settings, Announce when the sound quality changes: says when the sound is lowered because the connection struggles, and when it is back to full quality.
- Settings, Mute the remote PC while you are not controlling it: while your keys are on this PC you hear nothing from the remote PC, and it stays connected with no sound sent at all, so it uses almost no data. Control Shift Enter brings the sound straight back.
- File, Restart remote PC and reconnect restarts the remote PC and reconnects when it is back.
- File, Saved PCs: choose one to fill in its address, port and password. File, Save this PC remembers the address, port and password; File, Forget the chosen saved PC removes one.
- Listen-only password, in Host mode: anyone who connects with it hears the PC but cannot control it. Up to 100 at once. Each listener uses about 0.7 megabits per second of the host's upload while sound plays.

Running the host as a Windows service
- In Host mode, turn on Settings, Run as a Windows service. It asks for administrator permission once.
- The service starts with Windows, before anyone signs in, and has full system access. You can use the lock screen, sign in, answer administrator prompts and send Control Alt Delete with Control Alt End.
- Anyone who knows the Kova password gets that access too, so use a long password.
- While the service runs, Start hosting in the File menu becomes Apply settings to the service. Press it after changing the port or passwords.
- The service runs the very copy of Kova you turned it on from, where it is, for example in Documents or an apps folder, so leave that copy where it is. Kova only copies itself to Program Files for the service when it was started from a network drive, a USB stick or a temporary folder, and a service set up in Program Files by an older Kova stays there.
- It never updates by itself: when you update Kova on that PC with Check for updates, the copy you ran is updated in place and restarted, and the service, which runs that same copy, restarts into it straight away, without asking. A service in Program Files follows to the same version.
- Turn off Settings, Run as a Windows service to remove it completely.

More settings
- Enable logging: writes Kova-log.txt next to Kova (the service writes Kova-service-log.txt; if the folder cannot be written, both go to AppData, Local, Kova), with a line every second about the sound on that PC. Turn it on on both PCs and send both files when something sounds wrong. What you type is never logged.
- Capture sound from (Host mode): which output's sound the other PCs hear. Windows default follows whatever the default output is.
- Output device: where the remote PC's sound plays here.

How it keeps delay down
- Sound is Opus at 48 kHz stereo, up to 510 kilobits per second, in 5 millisecond packets over UDP, never waited for or resent. Opus adds about 2.5 milliseconds.
- Sound quality (Control mode): Variable follows the connection, lowering the bitrate until the sound fits, down to 6 kilobits per second, one step at most every three quarters of a second. Or lock it to one bitrate and it never changes. Above 16 kilobits per second it is tuned for music, from 16 down for speech. The lowest steps send longer packets (up to 60 milliseconds), so they fit even dial-up, at the cost of more delay.
- File, Streaming says the bitrate, whether it is locked or variable, and the audio delay.
- It always heads back to live: nothing old is ever played. The buffer covers how unevenly packets arrived over the last 3 seconds, never more than 40 milliseconds; a stall never raises it, and whatever piles up behind a stall is skipped the moment it lands.
- Catch up by fast-forwarding (Control mode): instead of skipping, the sound plays at 1.5x, 2x or 4x at its own pitch until it has caught up.
- Packets that arrive out of order are put back in order. A packet that never comes is filled in by Opus, smoothly.
- The pitch never changes.
- Keys go over TCP with no batching, so each key is sent the moment it is pressed.
- The window title shows the ping and the total audio delay. Press NVDA T to hear it. Run tailscale ping with the host's name: if it says via DERP, the connection is relayed and slower; a direct connection is best.

Updates
- About, Check for updates gets the newest version from github.com/ce2004/kova, checks the download, swaps it in and restarts. A host keeps hosting while it downloads, then restarts hosting; a connected PC reconnects.
- To update the cloud PC, use File, Update the remote PC from your side, or control it and use Check for updates there. Your side says the remote PC is updating, not that the connection broke, and says when it is back on the new version. A restart, a shutdown or stopping hosting are said the same way.
- If the connection drops for any reason, Kova keeps trying every second until it is back or you press Disconnect.

Security
- Both PCs prove they know the password before anything is sent, and wrong passwords are slowed down.
- After that, keys, audio and messages are encrypted with keys that are new for every connection, and anything changed on the way is thrown away.
- Use a long password if the port is open to the internet.

Limits
- Control Alt Delete and Windows L on your keyboard are kept by your own PC. Use Control Alt End for the remote PC.
- Without the service, the remote PC's lock screen and administrator prompts cannot be used from Kova.

Releasing
- Add a section to CHANGES.txt: the version number on its own line, then one change per line.
- Push a tag such as v1.0.1. GitHub builds both versions and publishes the release.

About the audio device
- It is VB-Cable by VB-Audio, free donationware: www.vb-cable.com. Its licence does not allow other programs to install it silently, so you press its Install Driver button yourself. Kova downloads it from vb-audio.com, checks it is signed by VB-Audio, then names it Kova and makes it the default output.
- If Windows needs a restart after installing it, restart and start hosting: Kova finishes the setup by itself.

Building
- build.bat builds this PC's own kind (arm64 or x64) quickly, in seconds, without trimming or compression. build.bat arm64 or build.bat x64 picks one. build.bat full builds both exactly as a release is built.
- If Kova is running from bin, the new build replaces it, closes the old copy and restarts it, still hosting or connected.
- Kova.exe --selftest runs a host and client on this PC and writes the result to tailremote-selftest.txt in the temp folder.
- Kova.exe --licence writes the NVDA controller client licence beside the exe.
