// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Ampr;

public static class AmprExports
{
    private const int CommandBufferHeaderSize = 0x28;
    private const ulong CommandBufferSelfOffset = 0x00;
    private const ulong CommandBufferDataOffset = 0x08;
    private const ulong CommandBufferSizeOffset = 0x10;
    private const ulong CommandBufferAux0Offset = 0x18;
    private const ulong CommandBufferAux1Offset = 0x20;
    private const ulong ReadFileRecordSize = 0x30;
    private static readonly ConcurrentDictionary<ulong, CommandBufferState> _commandBuffers = new();

    private sealed class CommandBufferState
    {
        public ulong Buffer;
        public ulong Size;
        public ulong WriteOffset;
    }

    [SysAbiExport(
        Nid = "8aI7R7WaOlc",
        ExportName = "sceAmprCommandBufferConstructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferConstructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var buffer = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!InitializeCommandBuffer(ctx, commandBuffer, buffer, size, aux0: 0, aux1: 0, clear: true))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "ctor", commandBuffer, buffer, size);
        ctx[CpuRegister.Rax] = commandBuffer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "a8uLzYY--tM",
        ExportName = "sceAmprAprCommandBufferConstructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferConstructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var aux0 = ctx[CpuRegister.Rsi];
        var aux1 = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var buffer = 0UL;
        var size = 0UL;
        _ = ctx.TryReadUInt64(commandBuffer + CommandBufferDataOffset, out buffer);
        _ = ctx.TryReadUInt64(commandBuffer + CommandBufferSizeOffset, out size);

        if (!InitializeCommandBuffer(ctx, commandBuffer, buffer, size, aux0, aux1, clear: false))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "apr_ctor", commandBuffer, aux0, aux1);
        ctx[CpuRegister.Rax] = commandBuffer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Qs1xtplKo0U",
        ExportName = "sceAmprAprCommandBufferDestructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferDestructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!ctx.TryWriteUInt64(commandBuffer + CommandBufferAux0Offset, 0) ||
            !ctx.TryWriteUInt64(commandBuffer + CommandBufferAux1Offset, 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "apr_dtor", commandBuffer, 0, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "GuchCTefuZw",
        ExportName = "sceAmprCommandBufferDestructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferDestructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!WriteVisibleCommandBufferPointers(ctx, commandBuffer, buffer: 0, size: 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        _commandBuffers.TryRemove(commandBuffer, out _);
        TraceAmpr(ctx, "dtor", commandBuffer, 0, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "N-FSPA4S3nI",
        ExportName = "sceAmprCommandBufferSetBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferSetBuffer(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var buffer = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!WriteCommandBufferPointers(ctx, commandBuffer, buffer, size))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "set_buffer", commandBuffer, buffer, size);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "baQO9ez2gL4",
        ExportName = "sceAmprCommandBufferReset",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferReset(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadUInt64(commandBuffer + CommandBufferDataOffset, out var buffer) ||
            !ctx.TryReadUInt64(commandBuffer + CommandBufferSizeOffset, out var size) ||
            !WriteCommandBufferPointers(ctx, commandBuffer, buffer, size, writeOffset: 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "reset", commandBuffer, buffer, size);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ULvXMDz56po",
        ExportName = "sceAmprCommandBufferClearBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferClearBuffer(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetCommandBufferState(ctx, commandBuffer, out var buffer, out var size, out _) ||
            !WriteVisibleCommandBufferPointers(ctx, commandBuffer, buffer: 0, size: 0))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        _commandBuffers.TryRemove(commandBuffer, out _);
        TraceAmpr(ctx, "clear_buffer", commandBuffer, buffer, size);
        ctx[CpuRegister.Rax] = buffer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mQ16-QdKv7k",
        ExportName = "sceAmprAprCommandBufferReadFile",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferReadFile(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var fileId = unchecked((uint)ctx[CpuRegister.Rcx]);
        var destination = ctx[CpuRegister.R8];
        var size = ctx[CpuRegister.R9];

        if (commandBuffer == 0 || (destination == 0 && size != 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out var fileOffset))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!AmprFileRegistry.TryGetHostPath(fileId, out var hostPath) || !File.Exists(hostPath))
        {
            TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead: 0, hostPath, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var result = TryReadFileToGuestMemory(ctx, hostPath, fileOffset, destination, size, out var bytesRead);
        if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead, hostPath, result);
            return result;
        }

        if (!AppendReadFileRecord(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead, hostPath, (int)OrbisGen2Result.ORBIS_GEN2_OK);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool InitializeCommandBuffer(
        CpuContext ctx,
        ulong commandBuffer,
        ulong buffer,
        ulong size,
        ulong aux0,
        ulong aux1,
        bool clear)
    {
        if (clear)
        {
            Span<byte> header = stackalloc byte[CommandBufferHeaderSize];
            header.Clear();
            if (!ctx.Memory.TryWrite(commandBuffer, header))
            {
                return false;
            }
        }

        return ctx.TryWriteUInt64(commandBuffer + CommandBufferSelfOffset, commandBuffer) &&
               ctx.TryWriteUInt64(commandBuffer + CommandBufferAux0Offset, aux0) &&
               ctx.TryWriteUInt64(commandBuffer + CommandBufferAux1Offset, aux1) &&
               WriteCommandBufferPointers(ctx, commandBuffer, buffer, size, writeOffset: 0);
    }

    private static bool WriteCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size)
    {
        return WriteCommandBufferPointers(ctx, commandBuffer, buffer, size, writeOffset: 0);
    }

    private static bool WriteCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size, ulong writeOffset)
    {
        if (!WriteVisibleCommandBufferPointers(ctx, commandBuffer, buffer, size))
        {
            return false;
        }

        var state = _commandBuffers.GetOrAdd(commandBuffer, static _ => new CommandBufferState());
        lock (state)
        {
            state.Buffer = buffer;
            state.Size = size;
            state.WriteOffset = writeOffset;
        }

        return true;
    }

    private static bool WriteVisibleCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size)
    {
        return ctx.TryWriteUInt64(commandBuffer + CommandBufferSelfOffset, commandBuffer) &&
               ctx.TryWriteUInt64(commandBuffer + CommandBufferDataOffset, buffer) &&
               ctx.TryWriteUInt64(commandBuffer + CommandBufferSizeOffset, size);
    }

    private static bool TryGetCommandBufferState(
        CpuContext ctx,
        ulong commandBuffer,
        out ulong buffer,
        out ulong size,
        out CommandBufferState? state)
    {
        if (_commandBuffers.TryGetValue(commandBuffer, out state))
        {
            lock (state)
            {
                buffer = state.Buffer;
                size = state.Size;
            }

            return true;
        }

        if (ctx.TryReadUInt64(commandBuffer + CommandBufferDataOffset, out buffer) &&
            ctx.TryReadUInt64(commandBuffer + CommandBufferSizeOffset, out size))
        {
            state = _commandBuffers.GetOrAdd(commandBuffer, static _ => new CommandBufferState());
            lock (state)
            {
                state.Buffer = buffer;
                state.Size = size;
                state.WriteOffset = 0;
            }

            return true;
        }

        buffer = 0;
        size = 0;
        state = null;
        return false;
    }

    private static int TryReadFileToGuestMemory(
        CpuContext ctx,
        string hostPath,
        ulong fileOffset,
        ulong destination,
        ulong size,
        out ulong bytesRead)
    {
        bytesRead = 0;
        if (size == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (fileOffset > long.MaxValue)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        const int ChunkSize = 64 * 1024;
        var buffer = new byte[(int)Math.Min((ulong)ChunkSize, size)];

        try
        {
            using var stream = new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fileOffset >= (ulong)stream.Length)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            stream.Position = unchecked((long)fileOffset);

            while (bytesRead < size)
            {
                var request = (int)Math.Min((ulong)buffer.Length, size - bytesRead);
                var read = stream.Read(buffer, 0, request);
                if (read <= 0)
                {
                    break;
                }

                if (!ctx.Memory.TryWrite(destination + bytesRead, buffer.AsSpan(0, read)))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                bytesRead += (ulong)read;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool AppendReadFileRecord(
        CpuContext ctx,
        ulong commandBuffer,
        uint fileId,
        ulong destination,
        ulong size,
        ulong fileOffset,
        ulong bytesRead)
    {
        if (!TryGetCommandBufferState(ctx, commandBuffer, out var buffer, out var capacity, out var state) || state is null)
        {
            return false;
        }

        Span<byte> record = stackalloc byte[(int)ReadFileRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x00..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x04..], fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x08..], destination);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x18..], fileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x20..], bytesRead);

        lock (state)
        {
            if (state.Buffer == 0 || state.WriteOffset + ReadFileRecordSize > state.Size)
            {
                return false;
            }

            if (!ctx.Memory.TryWrite(state.Buffer + state.WriteOffset, record))
            {
                return false;
            }

            state.WriteOffset += ReadFileRecordSize;
        }

        return true;
    }

    private static void TraceAmpr(CpuContext ctx, string operation, ulong commandBuffer, ulong arg0, ulong arg1)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var returnRip = 0UL;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] ampr.{operation}: cmd=0x{commandBuffer:X16} arg0=0x{arg0:X16} arg1=0x{arg1:X16} ret=0x{returnRip:X16}");
    }

    private static void TraceAmprRead(
        CpuContext ctx,
        ulong commandBuffer,
        uint fileId,
        ulong destination,
        ulong size,
        ulong fileOffset,
        ulong bytesRead,
        string? hostPath,
        int result)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var returnRip = 0UL;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] ampr.read_file: cmd=0x{commandBuffer:X16} id=0x{fileId:X8} dst=0x{destination:X16} size=0x{size:X16} offset=0x{fileOffset:X16} read=0x{bytesRead:X16} result=0x{result:X8} path='{hostPath ?? string.Empty}' ret=0x{returnRip:X16}");
    }
}
