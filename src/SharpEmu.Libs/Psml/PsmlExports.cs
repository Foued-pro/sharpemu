// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Psml;

// PlayStation Machine Learning (PSML) — hosts the MFSR machine-learning super
// resolution upscaler (PSSR). The emulator does not model the ML accelerator, so
// context creation reports "not found" and titles fall back to their conventional
// upscaling path, exactly as they do on hardware configurations without the feature.
public static class PsmlExports
{
    // NID resolved by hashing scripts/ps5_names.txt (called by Ghost of Yotei during
    // renderer init, right before its startup deadlock). Returning the same 0x80020002
    // the unresolved-import path produced keeps the title's already-proven fallback
    // behavior while removing the unresolved-NID spam from the log.
    [SysAbiExport(
        Nid = "fccGInHrj8A",
        ExportName = "scePsmlMfsrCreateContext1100",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrCreateContext1100(CpuContext ctx)
    {
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
    }

    [SysAbiExport(
        Nid = "VGjrQa-WqdU",
        ExportName = "scePsmlMfsrGetContextBufferRequirement1100",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetContextBufferRequirement1100(CpuContext ctx)
    {
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
    }

    [SysAbiExport(
        Nid = "eWoKNeB6V-k",
        ExportName = "scePsmlMfsrCreateSharedResources",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrCreateSharedResources(CpuContext ctx)
    {
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
    }
}
