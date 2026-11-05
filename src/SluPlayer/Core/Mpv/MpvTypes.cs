using System.Runtime.InteropServices;

namespace SluPlayer.Core.Mpv;

// Mirrors include/mpv/client.h (client API 2.x). Layouts are for x64.

public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9,
}

public enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    Tick = 14,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

public enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

public enum MpvLogLevel
{
    None = 0,
    Fatal = 10,
    Error = 20,
    Warn = 30,
    Info = 40,
    V = 50,
    Debug = 60,
    Trace = 70,
}

public enum MpvError
{
    Success = 0,
    EventQueueFull = -1,
    NoMem = -2,
    Uninitialized = -3,
    InvalidParameter = -4,
    OptionNotFound = -5,
    OptionFormat = -6,
    OptionError = -7,
    PropertyNotFound = -8,
    PropertyFormat = -9,
    PropertyUnavailable = -10,
    PropertyError = -11,
    Command = -12,
    LoadingFailed = -13,
    AoInitFailed = -14,
    VoInitFailed = -15,
    NothingToPlay = -16,
    UnknownFormat = -17,
    Unsupported = -18,
    NotImplemented = -19,
    Generic = -20,
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct MpvNode
{
    [FieldOffset(0)] public nint String;      // char*
    [FieldOffset(0)] public int Flag;
    [FieldOffset(0)] public long Int64;
    [FieldOffset(0)] public double Double;
    [FieldOffset(0)] public nint List;        // mpv_node_list*
    [FieldOffset(0)] public nint ByteArray;   // mpv_byte_array*
    [FieldOffset(8)] public MpvFormat Format;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvNodeList
{
    public int Num;
    public nint Values; // mpv_node*
    public nint Keys;   // char**
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvByteArray
{
    public nint Data;
    public nuint Size;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public nint Data;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEventProperty
{
    public nint Name; // const char*
    public MpvFormat Format;
    public nint Data;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEventLogMessage
{
    public nint Prefix;
    public nint Level;
    public nint Text;
    public MpvLogLevel LogLevel;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEventStartFile
{
    public long PlaylistEntryId;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEventHook
{
    public nint Name;
    public ulong Id;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEventCommand
{
    public MpvNode Result;
}
