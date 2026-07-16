// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Threading;

namespace SharpEmu.Libs.Audio;

// ACM (Audio Codec Memory) manages the direct-memory pool the PS5 audio
// stack decodes into. The emulator's codecs run on the host and allocate
// host-side, so a context here is bookkeeping only — but creation must
// succeed: Ghost of Yotei's audio arena bootstrap aborts when any step of
// the ACM bring-up chain fails, leaving its global arena table null, and the
// mastering path dereferences that table unconditionally later.
public static class AcmExports
{
    private static int _nextContextId;

    // sceAcmContextCreate(context* out, config*, flags, directMemoryStart,
    // directMemorySize). The caller reads back a 32-bit context id from *out
    // and treats any nonzero return as fatal.
    [SysAbiExport(
        Nid = "ZIXln2K3XMk",
        ExportName = "sceAcmContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextCreate(CpuContext ctx)
    {
        var contextAddress = ctx[CpuRegister.Rdi];
        if (contextAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        if (!ctx.TryWriteUInt32(contextAddress, contextId))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "jBgBjAj02R8",
        ExportName = "sceAcmContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextDestroy(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // DSP batch submission and synchronization. The emulator runs no ACM DSP
    // jobs (FFT/panner/reverb output stays silent), but Scream's workers trap
    // with int 0x41/0x42 asserts whenever a submission call reports failure,
    // so the whole batch surface must report success.
    [SysAbiExport(
        Nid = "WeZOIm8+8WI",
        ExportName = "sceAcmBatchInitialize",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchInitialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "Mk1xvQXIdkk",
        ExportName = "sceAcmBatchInitializeLite",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchInitializeLite(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "A5NXCXK5Gfc",
        ExportName = "sceAcmBatchStart",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStart(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "tW9W+CAG4FE",
        ExportName = "sceAcmBatchStartBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStartBuffer(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "8fe55ktlNVo",
        ExportName = "sceAcmBatchStartBuffers",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStartBuffers(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "S3BPrjCfZ90",
        ExportName = "sceAcmBatchStartMultiple",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStartMultiple(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "uqDIauipRbo",
        ExportName = "sceAcmBatchProcess",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchProcess(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "RLN3gRlXJLE",
        ExportName = "sceAcmBatchWait",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchWait(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }
}
