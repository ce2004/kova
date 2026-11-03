# Kova Helper: hands what NVDA speaks, its cut-offs, beeps, sounds and braille straight to Kova on this PC,
# so a PC controlling this one through Kova gets them at once. It also gives NVDA commands for Kova (category Kova in
# NVDA's Input gestures: connect to a saved PC, control it, send the clipboard and so on). They have no keys until the
# person gives them some: nothing here takes a key by itself.
#
# What it does and does not do:
# - It only sends. Nothing ever comes back from Kova, and it never runs or opens anything.
# - It only talks to local named pipes, never the network: \\.\pipe\KovaNvda-<session> for speech (there only while this
#   PC hosts) and \\.\pipe\KovaCommand-<session> for the commands (there whenever a Kova window runs). Before sending
#   anything it checks the pipe is owned by this user, SYSTEM or Administrators (no other signed-in person can make one
#   that passes) and served by a program called Kova.
# - A command is two numbers and nothing else (which command, and which saved PC): never text, never a password.
# - It never slows NVDA down: NVDA's threads only copy what NVDA said onto a bounded queue (the oldest go if Kova is
#   stuck), and one background thread does all the reading, encoding and writing. Everything waiting is written in
#   one go, and speech that a later cut-off would silence anyway is dropped before it is sent, so a console pouring
#   out text, or a long document read aloud, never builds up a backlog. While Kova is not running it tries again
#   quietly every few seconds and nothing is queued.
#
# Python standard library and NVDA's own modules only. Works with NVDA 2024.1 (Python 3.11, 32-bit) through 2026.x
# (Python 3.13, 64-bit).

import collections
import ctypes
import os
import re
import struct
import threading
from ctypes import wintypes

import globalPluginHandler
from logHandler import log
from scriptHandler import script

try:
	import addonHandler
	_VERSION = addonHandler.getCodeAddon().version
except Exception:
	_VERSION = "1.0.0"

PIPE_BASE = "\\\\.\\pipe\\KovaNvda-"
COMMAND_PIPE_BASE = "\\\\.\\pipe\\KovaCommand-"
COMMANDS_WAITING_MAX = 4  # key presses waiting to go to Kova; more are left out (Kova takes 5 a second at most)
RETRY_SECONDS = 3.0
REFUSED_RETRY_SECONDS = 15.0
KEEPALIVE_SECONDS = 2.0
QUEUE_MAX = 2048
MAX_UNITS = 4000  # characters (UTF-16 units, as Kova counts them) in one speech or braille event
MAX_PARTS = 64
WRITE_MAX = 60000  # bytes in one write to the pipe (Kova's pipe holds 64 KB)

_KOVA_NAME = re.compile(r"(?i)^kova[^\\/]*\.exe(\.old[^\\/]*)?$")
_WAVE_NAME = re.compile(r"^[A-Za-z0-9_-]{1,40}$")

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_adv = ctypes.WinDLL("advapi32", use_last_error=True)

_INVALID = ctypes.c_void_p(-1).value
_k32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
_k32.CreateFileW.restype = ctypes.c_void_p
_k32.WriteFile.argtypes = [ctypes.c_void_p, ctypes.c_char_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
_k32.WriteFile.restype = wintypes.BOOL
_k32.CloseHandle.argtypes = [ctypes.c_void_p]
_k32.CloseHandle.restype = wintypes.BOOL
_k32.CancelIoEx.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
_k32.CancelIoEx.restype = wintypes.BOOL
_k32.GetNamedPipeServerProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.ULONG)]
_k32.GetNamedPipeServerProcessId.restype = wintypes.BOOL
_k32.ProcessIdToSessionId.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
_k32.ProcessIdToSessionId.restype = wintypes.BOOL
_k32.GetCurrentProcessId.restype = wintypes.DWORD
_k32.GetCurrentProcess.restype = ctypes.c_void_p
_k32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
_k32.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
_k32.LocalFree.argtypes = [ctypes.c_void_p]
_k32.LocalFree.restype = ctypes.c_void_p
_k32.FlushFileBuffers.argtypes = [ctypes.c_void_p]
_k32.FlushFileBuffers.restype = wintypes.BOOL
_k32.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
_k32.WaitNamedPipeW.restype = wintypes.BOOL
_u32 = ctypes.WinDLL("user32", use_last_error=True)
_u32.AllowSetForegroundWindow.argtypes = [wintypes.DWORD]
_u32.AllowSetForegroundWindow.restype = wintypes.BOOL
_adv.GetSecurityInfo.argtypes = [ctypes.c_void_p, ctypes.c_int, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
_adv.GetSecurityInfo.restype = wintypes.DWORD
_adv.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
_adv.ConvertSidToStringSidW.restype = wintypes.BOOL
_adv.OpenProcessToken.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p)]
_adv.OpenProcessToken.restype = wintypes.BOOL
_adv.GetTokenInformation.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
_adv.GetTokenInformation.restype = wintypes.BOOL


class _PROCESSENTRY32W(ctypes.Structure):
	_fields_ = [
		("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD), ("th32ProcessID", wintypes.DWORD),
		("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
		("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", ctypes.c_long), ("dwFlags", wintypes.DWORD),
		("szExeFile", ctypes.c_wchar * 260),
	]


_k32.Process32FirstW.argtypes = [ctypes.c_void_p, ctypes.POINTER(_PROCESSENTRY32W)]
_k32.Process32FirstW.restype = wintypes.BOOL
_k32.Process32NextW.argtypes = [ctypes.c_void_p, ctypes.POINTER(_PROCESSENTRY32W)]
_k32.Process32NextW.restype = wintypes.BOOL

FILE_WRITE_DATA = 0x0002
FILE_READ_ATTRIBUTES = 0x0080
READ_CONTROL = 0x00020000
SYNCHRONIZE = 0x00100000
OPEN_EXISTING = 3


def _sidString(psid):
	s = ctypes.c_void_p()
	if not _adv.ConvertSidToStringSidW(psid, ctypes.byref(s)):
		return None
	try:
		return ctypes.wstring_at(s.value)
	finally:
		_k32.LocalFree(s)


def _mySid():
	tok = ctypes.c_void_p()
	if not _adv.OpenProcessToken(_k32.GetCurrentProcess(), 0x0008, ctypes.byref(tok)):
		return None
	try:
		need = wintypes.DWORD()
		_adv.GetTokenInformation(tok, 1, None, 0, ctypes.byref(need))
		buf = ctypes.create_string_buffer(need.value or 256)
		if not _adv.GetTokenInformation(tok, 1, buf, len(buf), ctypes.byref(need)):
			return None
		# TOKEN_USER starts with a pointer to the SID.
		psid = ctypes.cast(buf, ctypes.POINTER(ctypes.c_void_p))[0]
		return _sidString(psid)
	finally:
		_k32.CloseHandle(tok)


def _processName(pid):
	snap = _k32.CreateToolhelp32Snapshot(0x2, 0)
	if not snap or snap == _INVALID:
		return None
	try:
		e = _PROCESSENTRY32W()
		e.dwSize = ctypes.sizeof(e)
		ok = _k32.Process32FirstW(snap, ctypes.byref(e))
		while ok:
			if e.th32ProcessID == pid:
				return e.szExeFile
			ok = _k32.Process32NextW(snap, ctypes.byref(e))
		return None
	finally:
		_k32.CloseHandle(snap)


def _pipeOwner(h):
	owner = ctypes.c_void_p()
	sd = ctypes.c_void_p()
	if _adv.GetSecurityInfo(h, 6, 0x1, ctypes.byref(owner), None, None, None, ctypes.byref(sd)) != 0:
		return None
	try:
		return _sidString(owner)
	finally:
		_k32.LocalFree(sd)


def _kovaServer(h, me):
	"""Kova's process number when the pipe is Kova's (owned by this user, SYSTEM or Administrators, and served by a
	program called Kova), else 0."""
	pid = wintypes.ULONG()
	if not _k32.GetNamedPipeServerProcessId(h, ctypes.byref(pid)):
		return 0
	name = _processName(pid.value)
	if not name or not _KOVA_NAME.match(name):
		return 0
	return pid.value if _pipeOwner(h) in (me, "S-1-5-18", "S-1-5-32-544") else 0


def _serverIsKova(h, me):
	return _kovaServer(h, me) != 0


def _units(s):
	return len(s.encode("utf-16-le", "surrogatepass")) // 2


def _utf8(s):
	return s.encode("utf-8", "replace")


def _cut(text, units):
	"""The start of text holding at most `units` UTF-16 units, and the rest."""
	# Nearly always the whole text fits: no character takes more than two units, so that is known without counting.
	if len(text) * 2 <= units or _units(text) <= units:
		return text, ""
	n = 0
	for i, ch in enumerate(text):
		n += 2 if ord(ch) > 0xFFFF else 1
		if n > units:
			return text[:i], text[i:]
	return text, ""


def _speakBodies(parts):
	"""Speech parts (text, pitch, capital, spelled) as Speak event bodies, each within Kova's limits."""
	bodies = []
	cur = []
	used = 0
	for text, pitch, capital, spelled in parts:
		while text:
			room = MAX_UNITS - used
			if room <= 0 or len(cur) >= MAX_PARTS:
				bodies.append(cur)
				cur, used = [], 0
				room = MAX_UNITS
			piece, text = _cut(text, room)
			if not piece:
				# A two-unit character that does not fit: start a new event.
				bodies.append(cur)
				cur, used = [], 0
				continue
			cur.append((piece, pitch, capital, spelled))
			used += _units(piece)
	if cur:
		bodies.append(cur)
	out = []
	for b in bodies:
		if not b:
			continue
		data = bytearray([1, len(b)])
		for text, pitch, capital, spelled in b:
			raw = _utf8(text)
			data += struct.pack("<BbH", (1 if capital else 0) | (2 if spelled else 0), max(-100, min(100, pitch)), len(raw))
			data += raw
		out.append(bytes(data))
	return out


def _coalesce(items):
	"""
	What is worth sending of everything waiting, in order. Speech before the last cut-off is dropped (NVDA cut it off
	already: sent, the other PC would only start it and stop again), and so are the cut-offs before that one. Only the
	newest braille matters: the display shows one thing at a time. Beeps and sounds all go, as NVDA played them.
	"""
	lastCancel = lastBraille = -1
	for i, it in enumerate(items):
		if it[0] == "c":
			lastCancel = i
		elif it[0] == "r":
			lastBraille = i
	out = []
	for i, it in enumerate(items):
		kind = it[0]
		if kind == "r" and i != lastBraille:
			continue
		if i < lastCancel and kind in ("s", "c"):
			continue
		out.append(it)
	return out


def _encode(item):
	kind = item[0]
	if kind == "s":
		return _speakBodies(_speechParts(item[1]))
	if kind == "c":
		return [b"\x02"]
	if kind == "b":
		_, hz, ms, left, right = item
		return [struct.pack("<BHHBB", 3, max(20, min(20000, hz)), max(1, min(5000, ms)), max(0, min(100, left)), max(0, min(100, right)))]
	if kind == "w":
		raw = item[1].encode("ascii")
		return [bytes([4, len(raw)]) + raw]
	if kind == "r":
		text, _ = _cut(item[1], MAX_UNITS)
		raw = _utf8(text)
		return [struct.pack("<BH", 5, len(raw)) + raw]
	return []


class _Link(threading.Thread):
	"""The one thread that talks to Kova. NVDA's threads only call put(), which never waits."""

	def __init__(self):
		super().__init__(name="Kova Helper", daemon=True)
		self._queue = collections.deque()
		self._lock = threading.Lock()
		self._lostCancel = False
		self._wake = threading.Event()
		self._stopping = False
		self._handle = None
		self.connected = False
		self._me = _mySid()
		self._warned = False

	def put(self, item):
		if not self.connected:
			return
		with self._lock:
			if len(self._queue) >= QUEUE_MAX:
				# Kova is stuck: the oldest goes. A cut-off that goes is still sent, first, so the other PC never
				# carries on with speech this NVDA cut off.
				if self._queue.popleft()[0] == "c":
					self._lostCancel = True
			self._queue.append(item)
		self._wake.set()

	def _take(self):
		"""Everything waiting, worth sending, in order (see _coalesce)."""
		with self._lock:
			items = list(self._queue)
			self._queue.clear()
			lost, self._lostCancel = self._lostCancel, False
		if lost:
			items.insert(0, ("c",))
		return _coalesce(items)

	def stop(self):
		self._stopping = True
		self.connected = False
		h = self._handle
		if h:
			_k32.CancelIoEx(h, None)
		self._wake.set()

	def _pipeName(self):
		return PIPE_BASE + str(_session())

	def _connect(self):
		h = _k32.CreateFileW(self._pipeName(), FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE, 0, None, OPEN_EXISTING, 0, None)
		if not h or h == _INVALID:
			return RETRY_SECONDS
		if not _serverIsKova(h, self._me):
			_k32.CloseHandle(h)
			if not self._warned:
				self._warned = True
				log.warning("Kova Helper: the Kova pipe is served by something that is not Kova, so nothing is sent to it")
			return REFUSED_RETRY_SECONDS
		self._handle = h
		v = "".join(c for c in str(_VERSION) if c.isdigit() or c == ".")[:20] or "0"
		if not self._write(bytes([0, len(v)]) + v.encode("ascii")):
			self._drop()
			return RETRY_SECONDS
		with self._lock:
			self._queue.clear()
			self._lostCancel = False
		self.connected = True
		log.info("Kova Helper: connected to Kova")
		return 0

	def _write(self, body):
		return self._writeRaw(struct.pack("<I", len(body)) + body)

	def _writeRaw(self, data):
		h = self._handle
		if not h:
			return False
		done = wintypes.DWORD()
		return bool(_k32.WriteFile(h, data, len(data), ctypes.byref(done), None)) and done.value == len(data)

	def _sendBatch(self, items):
		"""Every item's frames, written in as few writes as fit: one call into Windows for a whole burst, not one per line."""
		buf = bytearray()
		for item in items:
			try:
				bodies = _encode(item)
			except Exception:
				log.debugWarning("Kova Helper: could not encode an item", exc_info=True)
				continue
			for b in bodies:
				buf += struct.pack("<I", len(b))
				buf += b
				if len(buf) >= WRITE_MAX:
					if not self._writeRaw(bytes(buf)):
						return False
					buf = bytearray()
		return not buf or self._writeRaw(bytes(buf))

	def _drop(self):
		was = self.connected
		self.connected = False
		h, self._handle = self._handle, None
		if h:
			_k32.CloseHandle(h)
		with self._lock:
			self._queue.clear()
			self._lostCancel = False
		if was:
			log.info("Kova Helper: Kova went away; trying again every few seconds")

	def run(self):
		while not self._stopping:
			try:
				if self._handle is None:
					wait = self._connect()
					if wait:
						self._wake.wait(wait)
						self._wake.clear()
					continue
				if not self._wake.wait(KEEPALIVE_SECONDS):
					# Nothing to send: a one-byte keepalive, so a Kova that went away is noticed now (and a new one
					# connected to) rather than at the next thing NVDA says, which would then be lost.
					if not self._write(b"\x00"):
						self._drop()
					continue
				self._wake.clear()
				# Whatever came in while the last batch was being written goes next, as one batch.
				while self._queue and self._handle is not None and not self._stopping:
					if not self._sendBatch(self._take()):
						self._drop()
						break
			except Exception:
				log.debugWarning("Kova Helper: link problem", exc_info=True)
				self._drop()
				self._wake.wait(RETRY_SECONDS)
		h, self._handle = self._handle, None
		if h:
			_k32.CloseHandle(h)


# ---- NVDA keys: Kova's commands, sent to the Kova window on this PC ----

# The command numbers Kova knows (Kova's NvdaKeyCommand): the frame is u32 length 2, then the command and its number.
CMD_CONNECT = 1  # the number is the saved PC, 1 to 9
CMD_CONTROL = 2
CMD_SEND_CLIPBOARD = 3
CMD_GET_CLIPBOARD = 4
CMD_DISCONNECT = 5
CMD_STATUS = 6
CMD_LIST = 7
CMD_SHOW = 8

GENERIC_WRITE = 0x40000000
ERROR_PIPE_BUSY = 231

NOT_RUNNING = "Kova isn't running."
NOT_TAKEN = "Kova didn't get that. Try again."
NOT_HERE = "Kova's keys don't work on this screen."


def _session():
	session = wintypes.DWORD()
	_k32.ProcessIdToSessionId(_k32.GetCurrentProcessId(), ctypes.byref(session))
	return session.value


def _say(text):
	"""Said by NVDA on its own main thread (the keys are sent from a thread of their own)."""
	try:
		import queueHandler
		import ui
		queueHandler.queueFunction(queueHandler.eventQueue, ui.message, text)
	except Exception:
		log.debugWarning("Kova Helper: could not say %r" % text, exc_info=True)


class _Keys(threading.Thread):
	"""Sends the commands NVDA keys ask for, one at a time. NVDA's main thread only calls put(), which never waits."""

	def __init__(self):
		super().__init__(name="Kova Helper keys", daemon=True)
		self._waiting = collections.deque()
		self._lock = threading.Lock()
		self._wake = threading.Event()
		self._stopping = False
		self._me = _mySid()
		self._warned = False

	def put(self, command, number=0):
		with self._lock:
			if len(self._waiting) >= COMMANDS_WAITING_MAX:
				return  # a key held down or pressed in a hurry: Kova would leave these out anyway
			self._waiting.append((command, number))
		self._wake.set()

	def stop(self):
		self._stopping = True
		self._wake.set()

	def send(self, command, number):
		"""None once Kova has the command (Kova then says what happened), else what to say instead."""
		name = COMMAND_PIPE_BASE + str(_session())
		h = None
		for _ in range(3):
			h = _k32.CreateFileW(name, GENERIC_WRITE | FILE_READ_ATTRIBUTES | READ_CONTROL, 0, None, OPEN_EXISTING, 0, None)
			if h and h != _INVALID:
				break
			h = None
			if ctypes.get_last_error() != ERROR_PIPE_BUSY:
				return NOT_RUNNING  # no Kova window on this PC (the pipe is only there while one runs)
			_k32.WaitNamedPipeW(name, 500)
		if h is None:
			return NOT_TAKEN
		try:
			pid = _kovaServer(h, self._me)
			if not pid:
				if not self._warned:
					self._warned = True
					log.warning("Kova Helper: the Kova keys pipe is served by something that is not Kova, so nothing is sent to it")
				return NOT_RUNNING
			# Show the Kova window brings it to the front: NVDA, which has the key press, lets Kova do that.
			_u32.AllowSetForegroundWindow(pid)
			frame = struct.pack("<IBB", 2, command, number)
			done = wintypes.DWORD()
			if not _k32.WriteFile(h, frame, len(frame), ctypes.byref(done), None) or done.value != len(frame):
				return NOT_TAKEN
			# Returns once Kova has read it: Kova only acts on a command whose connection then ends cleanly, so closing
			# straight after writing could otherwise lose it.
			_k32.FlushFileBuffers(h)
			return None
		finally:
			_k32.CloseHandle(h)

	def run(self):
		while not self._stopping:
			self._wake.wait()
			self._wake.clear()
			while not self._stopping:
				with self._lock:
					if not self._waiting:
						break
					command, number = self._waiting.popleft()
				try:
					said = self.send(command, number)
				except Exception:
					log.debugWarning("Kova Helper: a key could not be sent", exc_info=True)
					said = NOT_TAKEN
				if said:
					if said == NOT_RUNNING:
						with self._lock:
							self._waiting.clear()  # said once, not once for every key still waiting
					_say(said)


_commands = None


def _commandTypes():
	global _commands
	if _commands is None:
		try:
			from speech.commands import PitchCommand, CharacterModeCommand
			_commands = (PitchCommand, CharacterModeCommand)
		except Exception:
			_commands = ((), ())
	return _commands


def _speechParts(sequence):
	"""
	The words of an NVDA speech sequence, with the pitch, capital and spelling NVDA gave them. The words go exactly as
	NVDA was given them (before NVDA's own symbol processing), apostrophes, quotes and all.
	"""
	PitchCommand, CharacterModeCommand = _commandTypes()
	parts = []
	pitch = 0
	spelled = False
	for item in sequence:
		if isinstance(item, str):
			t = item.strip()
			if t:
				parts.append((t, pitch, len(t) == 1 and t.isupper(), spelled))
		elif PitchCommand and isinstance(item, PitchCommand):
			offset = getattr(item, "offset", 0) or 0
			multiplier = getattr(item, "multiplier", 1) or 1
			pitch = max(-100, min(100, int(round(offset + (multiplier - 1) * 50))))
		elif CharacterModeCommand and isinstance(item, CharacterModeCommand):
			spelled = bool(getattr(item, "state", False))
	return parts


class GlobalPlugin(globalPluginHandler.GlobalPlugin):
	# Kova's commands are listed under Kova in NVDA's Input gestures. None of them has a key: the person chooses their own.
	scriptCategory = "Kova"

	def __init__(self, *args, **kwargs):
		super().__init__(*args, **kwargs)
		self._link = None
		self._keys = None
		self._undo = []
		self._spoke = False
		self._braille = None
		self._cancellable = None
		try:
			import globalVars
			if getattr(getattr(globalVars, "appArgs", None), "secure", False):
				return  # the sign-in screen's NVDA: nothing to hand over, and no Kova window to command
			self._waves = os.path.normcase(os.path.normpath(os.path.join(globalVars.appDir, "waves")))
		except Exception:
			self._waves = None
		self._keys = _Keys()
		self._keys.start()
		# Speech is only ever sent to a hosting Kova: on a PC that only controls others there is no speech pipe, and this
		# link just looks for one quietly every few seconds, sending nothing.
		self._link = _Link()
		self._link.start()
		self._hookSpeech()
		self._hookBeeps()
		self._hookWaves()
		self._hookBraille()

	# ---- hooking into NVDA: its extension points where it has them, else a wrapper put back on exit ----

	def _register(self, point, handler):
		point.register(handler)
		self._undo.append(lambda: point.unregister(handler))

	def _wrap(self, module, name, before):
		original = getattr(module, name)

		def wrapper(*args, **kwargs):
			try:
				before(*args, **kwargs)
			except Exception:
				pass
			return original(*args, **kwargs)

		setattr(module, name, wrapper)
		self._undo.append(lambda: setattr(module, name, original))

	def _hookSpeech(self):
		try:
			from speech import extensions as ext
		except Exception:
			ext = None
		if ext is not None and hasattr(ext, "pre_speech"):
			self._register(ext.pre_speech, self._onSpeech)
		else:
			import speech
			import speech.speech as sp
			self._wrap(sp, "speak", lambda seq, *a, **k: self._onSpeech(speechSequence=seq))
			if getattr(speech, "speak", None) is not getattr(sp, "speak", None):
				self._wrap(speech, "speak", lambda seq, *a, **k: self._onSpeech(speechSequence=seq))
		if ext is not None and hasattr(ext, "speechCanceled"):
			self._register(ext.speechCanceled, self._onCancel)
		else:
			import speech
			import speech.speech as sp
			self._wrap(sp, "cancelSpeech", lambda *a, **k: self._onCancel())
			if getattr(speech, "cancelSpeech", None) is not getattr(sp, "cancelSpeech", None):
				self._wrap(speech, "cancelSpeech", lambda *a, **k: self._onCancel())

	def _hookBeeps(self):
		try:
			import tones
			if hasattr(tones, "decide_beep"):
				self._register(tones.decide_beep, self._decideBeep)
			else:
				self._wrap(tones, "beep", lambda hz, length, left=50, right=50, *a, **k: self._decideBeep(hz=hz, length=length, left=left, right=right))
		except Exception:
			log.debugWarning("Kova Helper: beeps are not handed over", exc_info=True)

	def _hookWaves(self):
		try:
			import nvwave
			if hasattr(nvwave, "decide_playWaveFile"):
				self._register(nvwave.decide_playWaveFile, self._decideWave)
			else:
				self._wrap(nvwave, "playWaveFile", lambda fileName, *a, **k: self._decideWave(fileName=fileName))
		except Exception:
			log.debugWarning("Kova Helper: sounds are not handed over", exc_info=True)

	def _hookBraille(self):
		try:
			import braille
			self._register(braille.pre_writeCells, self._onCells)
		except Exception:
			log.debugWarning("Kova Helper: braille is not handed over", exc_info=True)

	# ---- what NVDA does (these run on NVDA's own threads: quick, and never raise) ----

	def _onSpeech(self, speechSequence=None, **kwargs):
		link = self._link
		if link is None or not link.connected or not speechSequence:
			return
		try:
			# Only a copy here (NVDA changes the list in place right after this); it is read on the add-on's own thread.
			seq = list(speechSequence)
			self._expired(link, seq)
		except Exception:
			return
		self._spoke = True
		link.put(("s", seq))

	def _expired(self, link, seq):
		"""
		NVDA drops what it was about to say for a control that has lost the focus since, without a cut-off (its
		"cancellable" speech). When the last speech handed over was such speech and it has expired by the time
		NVDA says something new, the other PC is told to cut it off too, before the new words.
		"""
		last = self._cancellable
		self._cancellable = None
		for item in seq:
			# NVDA puts in a subclass (eventHandler's FocusLossCancellableSpeechCommand): found by its base's name.
			if any(c.__name__ == "_CancellableSpeechCommand" for c in type(item).__mro__):
				self._cancellable = item
		if last is not None and self._spoke:
			try:
				gone = bool(last.isCancelled)
			except Exception:
				gone = False
			if gone:
				self._spoke = False
				link.put(("c",))

	def _onCancel(self, **kwargs):
		link = self._link
		self._cancellable = None
		# Only after something was said: NVDA cancels often when there is nothing to cut off.
		if link is None or not link.connected or not self._spoke:
			return
		self._spoke = False
		link.put(("c",))

	def _decideBeep(self, hz=0, length=0, left=50, right=50, **kwargs):
		try:
			link = self._link
			if link is not None and link.connected:
				link.put(("b", int(round(hz)), int(length), int(left), int(right)))
		except Exception:
			pass
		return True  # a decider must say yes, or NVDA would not beep

	def _decideWave(self, fileName=None, **kwargs):
		try:
			link = self._link
			if link is not None and link.connected and fileName and self._waves:
				path = os.path.normcase(os.path.normpath(os.path.abspath(fileName)))
				folder, base = os.path.split(path)
				name, extension = os.path.splitext(os.path.basename(fileName))
				# Only NVDA's own sounds, by name: the controlling PC plays its own copy. Never a path.
				if folder == self._waves and extension.lower() == ".wav" and _WAVE_NAME.match(name):
					link.put(("w", name))
		except Exception:
			pass
		return True

	def _onCells(self, cells=None, rawText=None, **kwargs):
		link = self._link
		if link is None or not link.connected:
			return
		try:
			text = (rawText or "").rstrip()
			if text == self._braille:
				return  # the cursor blinking writes the same text again and again
			self._braille = text
			link.put(("r", text))
		except Exception:
			pass

	# ---- NVDA keys: each only queues the command (NVDA's main thread never waits on Kova) ----

	def _kova(self, command, number=0):
		keys = self._keys
		if keys is None:
			import ui
			ui.message(NOT_HERE)
			return
		keys.put(command, number)

	@script(description="Start controlling the PC in front (Control Shift Enter comes back)", category="Kova", speakOnDemand=True)
	def script_kovaControl(self, gesture):
		self._kova(CMD_CONTROL)

	@script(description="Send the clipboard to the PC in front", category="Kova", speakOnDemand=True)
	def script_kovaSendClipboard(self, gesture):
		self._kova(CMD_SEND_CLIPBOARD)

	@script(description="Get the remote PC's clipboard", category="Kova", speakOnDemand=True)
	def script_kovaGetClipboard(self, gesture):
		self._kova(CMD_GET_CLIPBOARD)

	@script(description="Disconnect from the PC in front", category="Kova", speakOnDemand=True)
	def script_kovaDisconnect(self, gesture):
		self._kova(CMD_DISCONNECT)

	@script(description="Say Kova's status: the PC in front, the delay, and the PCs in the background", category="Kova", speakOnDemand=True)
	def script_kovaStatus(self, gesture):
		self._kova(CMD_STATUS)

	@script(description="Say the saved PCs and their numbers", category="Kova", speakOnDemand=True)
	def script_kovaListPcs(self, gesture):
		self._kova(CMD_LIST)

	@script(description="Show the Kova window", category="Kova", speakOnDemand=True)
	def script_kovaShow(self, gesture):
		self._kova(CMD_SHOW)

	def terminate(self):
		for undo in reversed(self._undo):
			try:
				undo()
			except Exception:
				pass
		self._undo = []
		if self._link is not None:
			self._link.stop()
			self._link.join(1.0)
			self._link = None
		if self._keys is not None:
			self._keys.stop()
			self._keys.join(1.0)
			self._keys = None
		super().terminate()


def _connectScript(n):
	def script_kovaConnect(self, gesture):
		self._kova(CMD_CONNECT, n)
	script_kovaConnect.__name__ = "script_kovaConnect%d" % n
	return script(description="Connect to saved PC %d" % n, category="Kova", speakOnDemand=True)(script_kovaConnect)


# Saved PCs 1 to 9 (Control 1 to 9 in the Kova window): nine commands, each its own line in Input gestures.
for _n in range(1, 10):
	setattr(GlobalPlugin, "script_kovaConnect%d" % _n, _connectScript(_n))
del _n
