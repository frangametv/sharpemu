// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	private const string GuestExecutionProbeVariable = "SHARPEMU_TRACE_GUEST_EXEC_ADDRS";
	private static readonly object GuestExecutionProbeSync = new();
	private static readonly Dictionary<ulong, GuestExecutionProbeState> SharedGuestExecutionProbes = new();
	private ulong[] _guestExecutionProbeAddresses = Array.Empty<ulong>();

	internal sealed class GuestExecutionProbeState(byte originalByte)
	{
		public byte OriginalByte { get; } = originalByte;
		public int Registrations { get; private set; } = 1;
		public bool Restored { get; private set; }
		public bool Logged { get; private set; }

		public void Register() => Registrations++;

		public void MarkRestored() => Restored = true;

		public bool TryMarkLogged()
		{
			if (Logged)
			{
				return false;
			}

			Logged = true;
			return true;
		}

		public bool Release()
		{
			if (Registrations > 0)
			{
				Registrations--;
			}

			return Registrations == 0 && Restored;
		}
	}

	private unsafe void InstallGuestExecutionProbes()
	{
		RestoreGuestExecutionProbes();
		List<ulong> requestedAddresses = ParseDiagnosticAddresses(
			Environment.GetEnvironmentVariable(GuestExecutionProbeVariable));
		if (requestedAddresses.Count == 0)
		{
			return;
		}

		var installedAddresses = new List<ulong>(requestedAddresses.Count);
		foreach (ulong address in requestedAddresses)
		{
			lock (GuestExecutionProbeSync)
			{
				if (SharedGuestExecutionProbes.TryGetValue(address, out GuestExecutionProbeState? shared))
				{
					shared.Register();
					installedAddresses.Add(address);
					continue;
				}

				byte[] original = new byte[1];
				if (!TryReadHostBytes(address, original) || original[0] == 0xCC)
				{
					Console.Error.WriteLine(
						$"[LOADER][WARNING] guest-exec-probe install skipped address=0x{address:X16} readable={original[0] != 0} opcode=0x{original[0]:X2}");
					continue;
				}

				if (!TryWriteGuestExecutionProbeByte(address, 0xCC))
				{
					Console.Error.WriteLine(
						$"[LOADER][WARNING] guest-exec-probe install failed address=0x{address:X16}");
					continue;
				}

				SharedGuestExecutionProbes.Add(address, new GuestExecutionProbeState(original[0]));
				installedAddresses.Add(address);
				Console.Error.WriteLine(
					$"[LOADER][TRACE] guest-exec-probe armed address=0x{address:X16} opcode=0x{original[0]:X2}");
			}
		}

		_guestExecutionProbeAddresses = installedAddresses.ToArray();
	}

	private unsafe bool TryRecoverGuestExecutionProbe(
		uint exceptionCode,
		ulong exceptionAddress,
		void* contextRecord,
		ulong rip)
	{
		if (exceptionCode != 0x80000003u)
		{
			return false;
		}

		ulong probeAddress = 0;
		bool shouldLog = false;
		bool restoreFailed = false;
		lock (GuestExecutionProbeSync)
		{
			foreach ((ulong address, GuestExecutionProbeState shared) in SharedGuestExecutionProbes)
			{
				if (address != exceptionAddress && address + 1 != rip)
				{
					continue;
				}

				probeAddress = address;
				if (!shared.Restored)
				{
					restoreFailed = !TryWriteGuestExecutionProbeByte(address, shared.OriginalByte);
					if (!restoreFailed)
					{
						shared.MarkRestored();
					}
				}
				shouldLog = !restoreFailed && shared.TryMarkLogged();
				break;
			}
		}

		if (probeAddress == 0 || restoreFailed)
		{
			if (restoreFailed)
			{
				Console.Error.WriteLine(
					$"[LOADER][WARNING] guest-exec-probe restore failed address=0x{probeAddress:X16}");
				Console.Error.Flush();
			}
			return false;
		}

		WriteCtxU64(contextRecord, CTX_RIP, probeAddress);
		if (shouldLog)
		{
			ulong stackPointer = ReadCtxU64(contextRecord, CTX_RSP);
			ulong rax = ReadCtxU64(contextRecord, CTX_RAX);
			ulong rdi = ReadCtxU64(contextRecord, CTX_RDI);
			_ = TryReadHostQword(stackPointer, out ulong returnAddress);
			bool readRax = TryReadHostQword(rax, out ulong raxQword);
			ulong raxPlus10Qword = 0;
			bool readRaxPlus10 = rax <= ulong.MaxValue - 0x10 &&
				TryReadHostQword(rax + 0x10, out raxPlus10Qword);
			bool readRdi = TryReadHostQword(rdi, out ulong rdiQword);
			Console.Error.WriteLine(
				$"[LOADER][TRACE] guest-exec-probe hit address=0x{probeAddress:X16} " +
				$"rax=0x{rax:X16} rcx=0x{ReadCtxU64(contextRecord, CTX_RCX):X16} " +
				$"rdx=0x{ReadCtxU64(contextRecord, CTX_RDX):X16} rbx=0x{ReadCtxU64(contextRecord, CTX_RBX):X16} " +
				$"rsp=0x{stackPointer:X16} ret=0x{returnAddress:X16} rbp=0x{ReadCtxU64(contextRecord, CTX_RBP):X16} " +
				$"rsi=0x{ReadCtxU64(contextRecord, CTX_RSI):X16} rdi=0x{ReadCtxU64(contextRecord, CTX_RDI):X16} " +
				$"r8=0x{ReadCtxU64(contextRecord, CTX_R8):X16} r9=0x{ReadCtxU64(contextRecord, CTX_R9):X16} " +
				$"r12=0x{ReadCtxU64(contextRecord, CTX_R12):X16} r13=0x{ReadCtxU64(contextRecord, CTX_R13):X16} " +
				$"r14=0x{ReadCtxU64(contextRecord, CTX_R14):X16} r15=0x{ReadCtxU64(contextRecord, CTX_R15):X16} " +
				$"mem_rax={(readRax ? $"0x{raxQword:X16}" : "unreadable")} " +
				$"mem_rax_plus_10={(readRaxPlus10 ? $"0x{raxPlus10Qword:X16}" : "unreadable")} " +
				$"mem_rdi={(readRdi ? $"0x{rdiQword:X16}" : "unreadable")}");
			Console.Error.Flush();
		}
		return true;
	}

	private unsafe void RestoreGuestExecutionProbes()
	{
		lock (GuestExecutionProbeSync)
		{
			foreach (ulong address in _guestExecutionProbeAddresses)
			{
				if (!SharedGuestExecutionProbes.TryGetValue(address, out GuestExecutionProbeState? shared))
				{
					continue;
				}

				// Guest-created native threads can outlive the entry scope which armed
				// the probe. Keep an unhit, opt-in diagnostic breakpoint installed
				// across those scopes; the dedicated emulator child process owns the
				// mapping and will tear it down on exit. Once a probe fires, its byte is
				// restored in the exception handler and the final registration can drop
				// the shared record.
				if (!shared.Release())
				{
					continue;
				}
				SharedGuestExecutionProbes.Remove(address);
			}
		}

		_guestExecutionProbeAddresses = Array.Empty<ulong>();
	}

	private unsafe bool TryWriteGuestExecutionProbeByte(ulong address, byte value)
	{
		uint oldProtect = 0;
		if (!VirtualProtect((void*)address, 1u, 64u, &oldProtect))
		{
			return false;
		}

		try
		{
			*(byte*)address = value;
		}
		finally
		{
			VirtualProtect((void*)address, 1u, oldProtect, &oldProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, 1u);
		}

		return true;
	}
}
