using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

internal static class ReplayCrypto
{
    private const string KeyStorageKey = "qwerty";
    private const string IVStorageKey = "potato";

    private static readonly byte[] Key;
    private static readonly byte[] IV;

    static ReplayCrypto()
    {
        Key = DeriveKey(KeyStorageKey, 32);
        IV = DeriveKey(IVStorageKey, 16);
    }

    private static byte[] DeriveKey(string storageKey, int length)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(storageKey));
        if (hash.Length >= length)
            return hash.Take(length).ToArray();
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
            result[i] = hash[i % hash.Length];
        return result;
    }

    internal static byte[] Encrypt(byte[] plainText)
    {
        if (plainText == null)
        {
            Debug.Log("Encrypt Error: null input");
            return null;
        }
        try
        {
            using Aes aes = Aes.Create();
            aes.Key = Key;
            aes.IV = IV;
            using MemoryStream ms = new MemoryStream();
            using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                cs.Write(plainText, 0, plainText.Length);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Debug.Log("Encrypt Error: " + ex.Message);
            return null;
        }
    }

    internal static byte[] Decrypt(byte[] cipherText)
    {
        try
        {
            using Aes aes = Aes.Create();
            aes.Key = Key;
            aes.IV = IV;
            using MemoryStream ms = new MemoryStream();
            using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                cs.Write(cipherText, 0, cipherText.Length);
            return ms.ToArray();
        }
        catch (CryptographicException ex)
        {
            Debug.Log("Decrypt Error: " + ex.Message);
            return null;
        }
    }
}
