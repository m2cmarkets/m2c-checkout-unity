using System;
using System.Text;
using UnityEngine.Networking;

namespace M2C.Checkout.Internal
{
    internal sealed class BoundedDownloadHandler : DownloadHandlerScript
    {
        internal const int MaxBytes = 64 * 1024;
        private readonly byte[] _body = new byte[MaxBytes];
        private int _length;

        public bool TooLarge { get; private set; }

        public BoundedDownloadHandler() : base(new byte[8 * 1024])
        {
        }

        protected override void ReceiveContentLengthHeader(ulong contentLength)
        {
            if (contentLength > MaxBytes) TooLarge = true;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) return true;
            if (TooLarge || dataLength > MaxBytes - _length)
            {
                TooLarge = true;
                return false;
            }
            Buffer.BlockCopy(data, 0, _body, _length, dataLength);
            _length += dataLength;
            return true;
        }

        protected override byte[] GetData()
        {
            byte[] result = new byte[_length];
            Buffer.BlockCopy(_body, 0, result, 0, _length);
            return result;
        }

        protected override string GetText()
        {
            return Encoding.UTF8.GetString(_body, 0, _length);
        }

        internal bool ReceiveForTest(byte[] data)
        {
            return ReceiveData(data, data == null ? 0 : data.Length);
        }

        internal void DeclareLengthForTest(ulong length)
        {
            ReceiveContentLengthHeader(length);
        }

        internal string TextForTest()
        {
            return GetText();
        }
    }
}
