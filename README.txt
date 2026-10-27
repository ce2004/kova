Kova

Kova lets you control another Windows PC with your keyboard and hear everything it plays, with very little delay. It is designed for screen reader users and works fully with NVDA. Connections are encrypted from end to end, whether they go across a home network, a VPN, the internet, or a Kova Relay.


Downloads

- Kova-x64.exe is for PCs with Intel or AMD processors. Kova-arm64.exe is for PCs with ARM processors, such as Snapdragon laptops. Each is a single file; nothing else needs to be installed.
- KovaRelay-x64.exe and KovaRelay-arm64.exe are Kova Relay, an optional program for reaching PCs that are behind a router or firewall. See Kova Relay below.
- All downloads are on the releases page at github.com/ce2004/kova.


Getting started

Kova has two step-by-step windows, both in the File menu. Each asks one question at a time. Tab moves between the controls on a page, Enter continues, Back returns to the previous page, and Escape cancels.

To set up the PC you want to control:
1. Run Kova on that PC and choose File, Host this PC (Control H).
2. Choose how other computers should reach it:
   - Through a Kova Relay. Works from anywhere, and nothing needs opening on the PC or your router.
   - Directly, by the PC's own address. For computers on the same network or a VPN. Kova opens a port in Windows Firewall.
   - Both ways: through the relay from anywhere, and directly from the same network or a VPN.
3. Enter the port or the relay address, then the password. A listen-only password is optional.
4. Kova checks that the relay can be reached, then the last page says exactly what other computers enter. Its Copy the relay address and Copy this PC's address buttons put that address on the clipboard. Start hosting starts.

To connect from your own PC:
1. Choose File, Connect to a PC (Control N).
2. Choose a saved PC, or A new PC.
3. For a new PC, enter its address and password. The address can be the PC's name or IP address, or the address of the Kova Relay it hosts through. Kova works out which it is by itself. You can save the PC and give it a name.
4. Choose Connect.

Once connected:
- Control Shift Enter, in the Kova window, sends every key to the remote PC, including the Windows key, Alt Tab and your NVDA key. The mouse goes to the remote PC too. Press Control Shift Enter again to return to your own PC.
- File, Disconnect ends the connection. On the host, File, Stop hosting ends hosting.
- File, Connect to another PC connects to one more PC. The one you were using stays connected in the background, as with Switch to PC.


Saved PCs

- In Connect to a PC, the first page lists your saved PCs. Edit PC changes a PC's name, address and password. Delete PC removes it.
- Control 1 to Control 9 bring saved PC 1 to 9 to the front. The PC you were using stays connected in the background, silently, and its file transfers continue. File, Switch to PC lists the PCs in front, in the background and not connected, and can disconnect the ones in the background.
- File, Wake a saved PC and connect wakes a PC that is asleep or turned off, using Wake-on-LAN, and connects as soon as it answers. Kova learns how to wake a PC the first time you connect to it. The wake signal only reaches the PC's own network, so it is also sent through every PC you are connected to. The PC's network adapter must allow Wake-on-LAN.


Menus

The Kova window is intentionally empty: everything is in the menu bar. Press Alt, then use the arrow keys. On a setting, Enter turns it on or off, and the menu stays open so you can change several settings in a row. Settings with several choices, such as Audio quality, are submenus whose name includes the current choice.

The File and Settings menus only show what you can use right now, so they change as you connect and host.

File
- When this PC is neither hosting nor connected: Connect to a PC, Host this PC, Wake a saved PC and connect (once you have saved PCs), and Exit.
- While this PC hosts: Stop hosting, Measure this PC's internet speed, and Exit. While the Kova service hosts it, there is also Change how this PC is hosted, and Stop hosting removes the service.
- While connected: Disconnect, Connect to another PC, Switch to PC, Wake a saved PC and connect, Control remote PC, Show the remote screen, Your microphone to the remote PC, Restart remote PC and reconnect, Remote PC info, Update the remote PC, Install the Kova Helper on the remote PC, the streaming status, Measure this PC's internet speed, Measure the remote PC's internet speed, and Exit. On a listen-only connection, the items that need control are not shown.

Clipboard
- The current file transfer, the history of transfers, Send the clipboard (Control B), Send files (Control I), Send a folder and its contents (Control T), Get files from the remote PC (Control G), Get the remote PC's clipboard (Control Shift B), Stop the file transfer, the received files folder, and Accept text and files sent by the remote PC without asking.

Settings
- For controlling another PC, shown unless this PC hosts: Audio quality, Announce audio quality changes, Announce remote PC health alerts, Mute Kova when it is not the active window, Compensate for Wi-Fi interruptions, Output device, Remote NVDA speech, Microphone to use, Catch up by speeding up audio instead of skipping, and Notification sounds.
- For hosting, shown while this PC hosts or after it last hosted: Capture audio from, Play the controlling PC's microphone into, Start hosting when Windows starts (not while the Kova service is installed, which already starts with Windows), Run as a Windows service, Apply settings to the Kova service (while it is installed), Port editor, Set up audio device or Remove audio device, and Install the Kova Helper NVDA add-on.
- Always: Back up settings to a file, Restore settings from a backup (while nothing is connected or hosted in this window), and Diagnostic logging.

About
- Check for updates (Control U), View the changelog (Shift F1), Read the guide (F1), Keyboard shortcuts, About Kova, Licences and credits, Open the Kova website, Report a problem on GitHub, and Open the log file. Long text opens as a list: the arrow keys move a line at a time, Control Up and Control Down move between sections, and Control C copies a line.


Features

Remote screen
- File, Show the remote screen (Control Shift S) opens a window with the remote PC's screen. While the screen changes, it is shown as smooth video at up to 60 frames per second, using the remote PC's graphics hardware when available. When it stops changing, an exact picture is shown. NVDA plus R reads it with Windows text recognition.
- The title bar shows the frame rate. Escape returns to the main window and leaves the screen on; Control Shift S turns it off. F5 requests a fresh picture. With several monitors, the keys 1 to 9 choose a monitor, and Page Up and Page Down move between them.
- When the remote PC runs Kova as a service, the sign-in screen, the lock screen and administrator prompts are shown too.

Microphone
- File, Your microphone to the remote PC (Control Shift M) sends your microphone to the PC you control. It is off each time Kova starts. Settings, Microphone to use chooses the microphone.
- On the remote PC, your microphone plays into a virtual audio cable, so programs there can use it as their microphone. Use headphones, or the remote PC will hear its own sound through your microphone.
- A PC whose only sound device is the Kova audio device needs a second virtual cable for this, such as VB-Audio's free Hi-Fi Cable. Kova finds it automatically.

Remote NVDA speech
- Settings, Remote NVDA speech chooses how you hear the remote PC's NVDA: through the remote PC's audio, as text and braille spoken by your own NVDA, or both.
- As text, your NVDA speaks in your own voice and settings, follows the remote NVDA's pitch changes for capitals, and stops when the remote NVDA stops. Braille, beeps and sounds come through too. With text only, no audio is sent at all, which uses much less data.
- This requires the Kova Helper NVDA add-on on the remote PC. Install it there with Settings, Install the Kova Helper NVDA add-on, or from your PC with File, Install the Kova Helper on the remote PC. The add-on only communicates with Kova on the same PC and sends nothing over the internet by itself. NVDA restarts once to complete the installation, and Kova keeps the add-on up to date.

Clipboard and files
- Send the clipboard (Control B, or Control Shift Alt C) sends your clipboard to the other PC: text replaces the other PC's clipboard, and copied files go to its received files folder. Control Shift Alt C works even while you are controlling the remote PC, when every other key goes there. Get the remote PC's clipboard (Control Shift B) puts the remote clipboard on yours.
- Send files (Control I) and Send folders and their contents (Control T) send files to the other PC's received files folder, which is Downloads, Kova unless you choose another. In the folder window, pick several folders with Shift or Control and the arrow keys. Existing files are never overwritten.
- A new send waits its turn while another is going, and starts as soon as it is done. Only a new clipboard copy replaces an older one.
- Clipboard, Stop just one transfer (Control Shift T) lists every transfer to and from every PC, with the files and folders in each. Delete, or Alt S, stops only the line you are on and the rest carry on; Alt A stops everything; F5 says the line's progress; Escape closes it. Stopping one part of a transfer you send needs Kova 4.7 on the other PC.
- Get files from the remote PC (Control G) lists the remote PC's folders and drives. Enter opens a folder, Space selects files and folders, and Backspace goes up. Choose Get the checked files and folders (Alt G) to copy them all at once.
- Clipboard, Files shows what is being sent or received, the progress, speed and time remaining. Transfers interrupted by a dropped connection continue automatically when you reconnect, and whatever arrived is always kept.

Remote PC tools
- Remote PC info (Control Shift I) shows the remote PC's name, Windows version, signed-in user, uptime, processor, memory, battery, free disk space, Kova version and current connections.
- Restart remote PC and reconnect restarts the remote PC and reconnects when it is back.
- Update the remote PC updates Kova on the remote PC to the newest version, including the Kova service. The menu item says which version each PC has.
- The internet speed tests report download and upload speed, latency and jitter after about 20 seconds. The audio may break up while a test runs.
- Announce remote PC health alerts reports when the remote PC switches to or from battery power, when its battery is low, when its system drive is nearly full, when its processor has been busy for a full minute, and when it is nearly out of memory.

Listening only
- A host can have a listen-only password. Anyone who connects with it hears the PC but cannot control it, send files or use the clipboard. Up to 100 listeners can connect at once, each using about 0.7 megabits per second of the host's upload while audio plays.

Settings backup
- Settings, Back up settings to a file saves every setting and saved PC, including passwords, in one file protected by a password you choose. Restore settings from a backup loads them on this PC.


Running the host as a Windows service

- In Settings, turn on Run as a Windows service. Windows asks for administrator permission once.
- The service starts with Windows, before anyone signs in. You can use the sign-in screen, the lock screen and administrator prompts, and Control Alt End sends Control Alt Delete.
- Anyone who knows the Kova password has this access too, so use a strong password.
- While the service hosts, File, Change how this PC is hosted gives the service new passwords or a new way to be reached, and Settings, Apply settings to the Kova service gives it settings changed in the Settings menu, such as Capture audio from or Diagnostic logging.
- File, Stop hosting removes the service, after asking first.
- The service only runs a copy of Kova that only administrators can change. If you turn it on from a folder of your own, Kova installs a copy in Program Files, Kova for the service.
- When you update Kova with Check for updates, the service updates to the same version straight away.
- Turn off Run as a Windows service to remove it completely.


Kova Relay

Kova Relay connects PCs that cannot reach each other directly, for example a PC behind a home router. Run it on any PC both can reach, such as a cloud server. It works like NVDA Remote: there are no names to manage, and the password pairs the two PCs.

How it works
- A PC hosting through the relay connects out to it and stays connected. It opens no ports of its own.
- When you connect, you enter the relay address and the password of the PC you want. The relay connects you to the PC hosting with that password.
- Each PC you host needs its own password. The password you type decides which PC you reach, so one relay can serve any number of PCs.
- The password never reaches the relay. Kova sends a code made from it, which takes deliberate effort to reverse. Even so, use a long password for any PC reachable through a relay.
- If a PC is already hosting on the relay with a password, another PC cannot host with the same password until the first one disconnects.

What the relay can and cannot see
- It sees the addresses of the PCs that connect, when, and how much data passes.
- It never sees passwords, keystrokes, audio, the screen, files, the clipboard or NVDA speech. All of that is encrypted between the two PCs, and each host's identity is verified by your PC, so a relay cannot impersonate a host.

Running Kova Relay
- It uses port 47200 unless you change it. Its settings are in ProgramData, KovaRelay, and only an administrator can change them, so Windows asks for permission when you change the port.
- It runs in the notification area. Enter or a double click on its icon opens its window. Tab moves through the status, the port, Use this port, Port editor, Stop or Start the relay, the hosts connected now, Start with Windows, Run as a Windows service, Open the log and Exit. Escape hides the window.
- Port editor opens and closes the relay's ports in Windows Firewall and removes old rules. It never closes the port in use.
- Run as a Windows service starts the relay with Windows, before anyone signs in, with limited rights. The relay must then be in a folder only administrators can change, such as Program Files.
- The relay limits the connections from any one address, closes connections that send invalid data or nothing at all, and keeps its memory use bounded. Its log never contains passwords or session content.


Updates

- About, Check for updates downloads the newest version from github.com/ce2004/kova, verifies that it is signed by Kova's developers and that the download matches exactly, then replaces Kova and restarts it. A host keeps hosting and a connected PC reconnects.
- To update a remote PC, use File, Update the remote PC. Kova tells you when the remote PC is updating and when it is back.
- If a connection drops for any reason, Kova tries again every second until it reconnects or you choose Disconnect.


Audio and latency

- Audio is Opus at 48 kHz stereo, up to 510 kilobits per second, sent in 5 millisecond packets that are never delayed or resent.
- Audio quality: Variable follows the connection, lowering the bitrate when needed, down to 6 kilobits per second. You can also lock it to one bitrate.
- Kova always stays live: old audio is never played. The buffer adapts to how evenly packets arrive and never exceeds 40 milliseconds. After a stall, the delayed audio is skipped, or, with Catch up by speeding up audio instead of skipping, played faster at the same pitch until it has caught up.
- Keystrokes are sent the moment they are pressed.
- The window title shows the latency and the total audio delay. Press NVDA T to hear it.


Security

- Both PCs prove they know the password before anything is sent, and wrong passwords are slowed down. Each host has a lasting identity that signs every connection. Your PC remembers it and asks before connecting if it ever changes.
- After that, everything is encrypted with keys that are new for every connection, and anything altered in transit is rejected.
- An address that keeps guessing is blocked for a while. PCs that have never connected before get 30 wrong passwords per minute, 200 per hour and 1,000 per day in total. PCs that have connected before are always let in.
- A listen-only connection receives audio only: no keys, mouse, files, clipboard, microphone or NVDA text.
- While the remote PC is locked, Get files and Get the remote PC's clipboard wait until someone signs in.
- Received files are marked as coming from the internet, and file names that disguise a file's type are made safe.
- Updates are signed and verified before they are installed.
- Use a long password for any PC that can be reached from the internet or through a relay.


Limits

- Control Alt Delete and Windows L on your own keyboard always act on your own PC. Use Control Alt End to send Control Alt Delete to the remote PC.
- Without the Kova service, the remote PC's lock screen and administrator prompts cannot be used.
- Remote NVDA text requires the Kova Helper add-on, and does not include the sign-in screen, where a separate copy of NVDA runs.
- The microphone only goes to a PC you connected to with its full password, and a PC plays one controlling PC's microphone at a time.


About the audio device

- A PC without a sound card, such as a cloud server, needs a virtual audio device. Settings, Set up audio device installs VB-Cable by VB-Audio (donationware, www.vb-cable.com), names it Kova and makes it the default output. Its licence requires that you press its Install Driver button yourself. Remove audio device removes it.
- If Windows asks for a restart after installing it, restart and start hosting; Kova completes the setup.


For developers

- build.bat builds this PC's architecture quickly, with Kova Relay beside Kova. build.bat x64 or build.bat arm64 chooses one; build.bat full builds both exactly as a release is built. If Kova is running from the bin folder, the new build replaces it and restarts it.
- Tests: Kova.exe --selftest, --securitytest and --chaostest; KovaRelay.exe --selftest; and Kova.exe --relaytest PORT against a Kova Relay running on this PC. Each writes its result to a file in the temp folder.
- To release, add a section to CHANGES.txt (the version on its own line, then one change per line) and push a tag such as v4.6.0. GitHub builds, signs and publishes both programs.
- Kova.exe --licence writes the NVDA controller client licence beside the program.
