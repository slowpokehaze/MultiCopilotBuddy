using System.Runtime.InteropServices;
using System.Text;

namespace MCB.Core.Security;

/// <summary>
/// DPAPI-шифрование через P/Invoke (crypt32.dll) — без зависимости от пакета
/// System.Security.Cryptography.ProtectedData, которого нет на машине без SDK.
/// Область — CurrentUser: блоб не расшифровывается под другим пользователем Windows.
/// </summary>
public static class DpapiProtector
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    /// <summary>Дополнительная энтропия: блобы MCB не расшифровываются другим софтом.</summary>
    private static readonly byte[] Entropy = "MCB.MultiAccount.v1"u8.ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    /// <summary>Шифрует открытый текст в base64-блоб. Пустая строка → пустая строка.</summary>
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(ProtectBytes(bytes));
    }

    /// <summary>Расшифровывает base64-блоб. Пустая строка → пустая строка.</summary>
    public static string Unprotect(string cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64))
        {
            return string.Empty;
        }

        var cipher = Convert.FromBase64String(cipherBase64);
        return Encoding.UTF8.GetString(ProtectBytes(cipher, protect: false));
    }

    private static byte[] ProtectBytes(byte[] bytes, bool protect = true)
    {
        var pin = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes, 0, pin, bytes.Length);
            var inBlob = new DataBlob { CbData = bytes.Length, PbData = pin };

            // pOptionalEntropy — указатель на DATA_BLOB с энтропией.
            var entropyPin = Marshal.AllocHGlobal(Math.Max(1, Entropy.Length));
            var entropyBlobPtr = IntPtr.Zero;
            try
            {
                Marshal.Copy(Entropy, 0, entropyPin, Entropy.Length);
                var entropyBlob = new DataBlob { CbData = Entropy.Length, PbData = entropyPin };
                entropyBlobPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DataBlob>());
                Marshal.StructureToPtr(entropyBlob, entropyBlobPtr, false);

                var ok = protect
                    ? CryptProtectData(ref inBlob, null, entropyBlobPtr, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, entropyBlobPtr, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);

                if (!ok)
                {
                    var what = protect ? "CryptProtectData" : "CryptUnprotectData";
                    throw new InvalidOperationException(
                        $"{what} failed (winerror {Marshal.GetLastWin32Error()})" +
                        (protect ? string.Empty : " — блоб создан другим пользователем Windows или другой энтропией."));
                }

                try
                {
                    var result = new byte[outBlob.CbData];
                    Marshal.Copy(outBlob.PbData, result, 0, outBlob.CbData);
                    return result;
                }
                finally
                {
                    if (outBlob.PbData != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(outBlob.PbData);
                    }
                }
            }
            finally
            {
                if (entropyBlobPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(entropyBlobPtr);
                }

                var zero = new byte[Entropy.Length];
                Marshal.Copy(zero, 0, entropyPin, Entropy.Length);
                Marshal.FreeHGlobal(entropyPin);
            }
        }
        finally
        {
            var zero = new byte[Math.Max(1, bytes.Length)];
            Marshal.Copy(zero, 0, pin, bytes.Length);
            Marshal.FreeHGlobal(pin);
        }
    }
}
