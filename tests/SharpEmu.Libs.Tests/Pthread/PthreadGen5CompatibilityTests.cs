// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadGen5CompatibilityTests
{
    private static readonly (string Nid, string Name, string Library)[] ExpectedExports =
    [
        ("2ozFS9GCs+A", "__sharpemu_gen5_thrd_current", "libc"),
        ("5qXct3c1skg", "__libcpp_mutex_lock", "libc"),
        ("4bp9gcNLwMI", "__libcpp_mutex_unlock", "libc"),
        ("fUs4X3mpTi4", "__sharpemu_gen5_cond_wait", "libc"),
        ("K953PF5u6Pc", "pthread_cond_reltimedwait_np", "libKernel"),
        ("enG9-gUJp70", "__libcpp_condvar_broadcast", "libc"),
        ("mKoTx03HRWA", "pthread_condattr_init", "libKernel"),
        ("EjllaAqAPZo", "pthread_condattr_setclock", "libKernel"),
        ("dJcuQVn6-Iw", "pthread_condattr_destroy", "libKernel"),
        ("ZMn3clnAGBA", "pthread_spin_init", "libKernel"),
        ("IJIggoPZExk", "pthread_spin_destroy", "libKernel"),
        ("pw+70ClLYlY", "pthread_spin_lock", "libKernel"),
        ("rCTGkBIHfPY", "pthread_spin_trylock", "libKernel"),
        ("LEfMMCT+SlM", "pthread_spin_unlock", "libKernel"),
        ("CfO+zWMbJJQ", "__sharpemu_gen5_thrd_detach", "libc"),
        ("FIs3-UQT9sg", "pthread_getschedparam", "libKernel"),
        ("Ucsu-OK+els", "pthread_attr_get_np", "libKernel"),
        ("vQm4fDEsWi8", "pthread_attr_getstack", "libKernel"),
        ("0qOtCR-ZHck", "pthread_attr_getstacksize", "libKernel"),
        ("E+tyo3lp5Lw", "pthread_attr_setdetachstate", "libKernel"),
        ("oxMp8uPqa+U", "pthread_set_name_np", "libKernel"),
        ("cfjAjVTFG6A", "pthread_suspend_user_context_np", "libKernel"),
        ("QRdE7dBfNks", "pthread_resume_user_context_np", "libKernel"),
        ("YkGOXpJEtO8", "pthread_get_user_context_np", "libKernel"),
        ("el9stmu6290", "pthread_set_user_context_np", "libKernel"),
    ];

    [Fact]
    public void ForkGen5PthreadCompatibilityExports_AreAllRegisteredExactlyOnce()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        foreach (var expected in ExpectedExports)
        {
            var export = Assert.Single(exports, candidate => candidate.Nid == expected.Nid);
            Assert.Equal(expected.Name, export.Name);
            Assert.Equal(expected.Library, export.LibraryName);
            Assert.NotEqual(Generation.None, export.Target & Generation.Gen5);
        }
    }

    [Fact]
    public void SpinTrylock_ReportsBusyUntilUnlocked()
    {
        const ulong memoryBase = 0x4_0000_0000;
        const ulong spinAddress = memoryBase + 0x100;
        var context = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = spinAddress;

        Assert.Equal(0, KernelPthreadExtendedCompatExports.PosixPthreadSpinInit(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PosixPthreadSpinTrylock(context));
        Assert.Equal(16, KernelPthreadExtendedCompatExports.PosixPthreadSpinTrylock(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PosixPthreadSpinUnlock(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PosixPthreadSpinTrylock(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PosixPthreadSpinUnlock(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PosixPthreadSpinDestroy(context));
    }
}
