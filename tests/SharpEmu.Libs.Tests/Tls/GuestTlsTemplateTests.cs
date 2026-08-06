// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Tls;

public sealed class GuestTlsTemplateTests
{
    [Fact]
    public void StartupReservationAcceptsTlsSpansLargerThanOneHostPage()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: new byte[0x20],
                memorySize: 0x1870,
                alignment: 0x10);

            Assert.Equal(0x1870UL, staticOffset);
            Assert.True(staticOffset <= GuestTlsTemplate.StartupStaticTlsReservation);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void StartupReservationAcceptsGtaVClassStaticTlsLayout()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: new byte[0x20],
                memorySize: 0x13570,
                alignment: 0x10);

            Assert.Equal(0x13570UL, staticOffset);
            Assert.True(staticOffset <= GuestTlsTemplate.StartupStaticTlsReservation);
            Assert.Equal(0UL, GuestTlsTemplate.StartupStaticTlsReservation & 0xFFFFUL);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void StartupReservationAcceptsExactBoundary()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: [],
                memorySize: GuestTlsTemplate.StartupStaticTlsReservation,
                alignment: 0x1000);

            Assert.Equal(GuestTlsTemplate.StartupStaticTlsReservation, staticOffset);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void StartupReservationRejectsLayoutPastBoundaryWithoutMutatingState()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                GuestTlsTemplate.RegisterModule(
                    moduleId: 1,
                    initImage: [],
                    memorySize: GuestTlsTemplate.StartupStaticTlsReservation + 1,
                    alignment: 1));

            Assert.Contains("Static TLS requires", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0UL, GuestTlsTemplate.StaticTlsSize);
            Assert.False(GuestTlsTemplate.TryGetStaticOffset(1, out _));
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }
}
