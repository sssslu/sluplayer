using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SluPlayer.Setup
{
    /// <summary>
    /// The release zip appended to the setup exe by build-installer.ps1:
    ///   [setup exe][zip][trailer: "SLUSETUP" | int64 zip offset | int64 zip length | int64 reserved]
    /// </summary>
    internal sealed class Payload : IDisposable
    {
        public const int TrailerSize = 32;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SLUSETUP");
        private readonly FileStream _file;

        public long Offset { get; }
        public long Length { get; }

        private Payload(FileStream file, long offset, long length)
        {
            _file = file;
            Offset = offset;
            Length = length;
        }

        /// <summary>Returns null when the exe carries no payload (the installed Uninstall.exe).</summary>
        public static Payload TryOpen(string exePath)
        {
            var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                if (fs.Length < TrailerSize + 1024) { fs.Dispose(); return null; }
                var t = new byte[TrailerSize];
                fs.Seek(-TrailerSize, SeekOrigin.End);
                ReadExactly(fs, t, 0, TrailerSize);
                for (int i = 0; i < Magic.Length; i++)
                    if (t[i] != Magic[i]) { fs.Dispose(); return null; }
                long offset = BitConverter.ToInt64(t, 8);
                long length = BitConverter.ToInt64(t, 16);
                if (offset <= 0 || length <= 0 || offset + length != fs.Length - TrailerSize)
                    throw new InvalidDataException("설치 파일 손상. 다시 다운로드 필요");
                return new Payload(fs, offset, length);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public ZipArchive OpenZip() => new ZipArchive(new SubStream(_file, Offset, Length), ZipArchiveMode.Read, leaveOpen: true);

        /// <summary>Copies the setup program itself (everything before the payload) - that becomes Uninstall.exe.</summary>
        public void WriteProgramOnly(string path)
        {
            using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                _file.Seek(0, SeekOrigin.Begin);
                var buf = new byte[1 << 16];
                long left = Offset;
                while (left > 0)
                {
                    int n = _file.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) throw new EndOfStreamException();
                    dst.Write(buf, 0, n);
                    left -= n;
                }
            }
        }

        public void Dispose() => _file.Dispose();

        private static void ReadExactly(Stream s, byte[] buf, int off, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buf, off, count);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
                count -= n;
            }
        }

        /// <summary>Read-only window [offset, offset+length) of a seekable stream.</summary>
        private sealed class SubStream : Stream
        {
            private readonly Stream _base;
            private readonly long _start;
            private readonly long _length;
            private long _pos;

            public SubStream(Stream baseStream, long start, long length)
            {
                _base = baseStream;
                _start = start;
                _length = length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _pos;
                set
                {
                    if (value < 0 || value > _length) throw new ArgumentOutOfRangeException(nameof(value));
                    _pos = value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                long left = _length - _pos;
                if (left <= 0) return 0;
                if (count > left) count = (int)left;
                _base.Seek(_start + _pos, SeekOrigin.Begin);
                int n = _base.Read(buffer, offset, count);
                _pos += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long target;
                switch (origin)
                {
                    case SeekOrigin.Begin: target = offset; break;
                    case SeekOrigin.Current: target = _pos + offset; break;
                    default: target = _length + offset; break;
                }
                Position = target;
                return _pos;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
