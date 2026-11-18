using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Oranda.Platform.Keyring;

/// <summary>
/// libsecret(Linux Secret Service 클라이언트)을 실행 중에 불러 쓴다. 가변 인자 함수는 쓰지 않고 GHashTable 을 받는
/// <c>*v_sync</c> 함수만 쓴다. 스키마 = <c>app.oranda.Credential</c>, 속성 <c>service</c>=<c>Oranda</c>·<c>account</c>=키.
/// 값 = 비밀 바이트의 base64. 비밀·키는 로그·예외 메시지에 쓰지 않는다. 동시 호출은 한 번에 하나만 한다.
/// </summary>
internal sealed class KeyringNative
{
    internal const string SchemaName = "app.oranda.Credential";
    internal const string ServiceValue = "Oranda";
    private const string GlibName = "libglib-2.0.so.0";
    private const string Label = "Oranda";
    private const string ProbeAccount = "oranda:probe";

    // SecretSchema: name(포인터) · flags(int, 8바이트 정렬) · attributes[32]{name 포인터, type int} · reserved int · reserved1~7 포인터.
    private const int AttributeOffset = 16;
    private const int AttributeSize = 16;
    private const int SchemaSize = AttributeOffset + (32 * AttributeSize) + 8 + (7 * 8);

    private static readonly Lazy<IntPtr> Schema = new(CreateSchema);

    private readonly Lock _gate = new();
    private readonly StoreV _store;
    private readonly LookupV _lookup;
    private readonly ClearV _clear;
    private readonly PasswordFree _passwordFree;
    private readonly HashTableNewFull _hashTableNew;
    private readonly HashTableInsert _hashTableInsert;
    private readonly HashTableUnref _hashTableUnref;
    private readonly StrDup _strdup;
    private readonly ErrorFree _errorFree;
    private readonly IntPtr _strHash;
    private readonly IntPtr _strEqual;
    private readonly IntPtr _free;

    private KeyringNative(IntPtr library, IntPtr glib)
    {
        _store = Bind<StoreV>(library, "secret_password_storev_sync");
        _lookup = Bind<LookupV>(library, "secret_password_lookupv_sync");
        _clear = Bind<ClearV>(library, "secret_password_clearv_sync");
        _passwordFree = Bind<PasswordFree>(library, "secret_password_free");
        _hashTableNew = Bind<HashTableNewFull>(glib, "g_hash_table_new_full");
        _hashTableInsert = Bind<HashTableInsert>(glib, "g_hash_table_insert");
        _hashTableUnref = Bind<HashTableUnref>(glib, "g_hash_table_unref");
        _strdup = Bind<StrDup>(glib, "g_strdup");
        _errorFree = Bind<ErrorFree>(glib, "g_error_free");
        _strHash = NativeLibrary.GetExport(glib, "g_str_hash");
        _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");
        _free = NativeLibrary.GetExport(glib, "g_free");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StoreV(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LookupV(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ClearV(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PasswordFree(IntPtr password);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr HashTableNewFull(IntPtr hash, IntPtr equal, IntPtr keyDestroy, IntPtr valueDestroy);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HashTableInsert(IntPtr table, IntPtr key, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HashTableUnref(IntPtr table);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr StrDup([MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ErrorFree(IntPtr error);

    /// <summary>
    /// 라이브러리를 불러오고 키링이 답하는지 한 번 확인한다(없는 항목 찾기 — 잠금 해제 창을 띄우지 않는다).
    /// 라이브러리·세션 버스·키링 중 하나라도 없으면 null. 예외를 내지 않는다.
    /// </summary>
    internal static KeyringNative? TryOpen(string libraryName, string? sessionBus)
    {
        // 세션 버스 주소가 없으면 libsecret 이 버스를 새로 띄우려 할 수 있다(X11 autolaunch). 키링 없음으로 본다.
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(sessionBus))
        {
            return null;
        }

        try
        {
            if (!NativeLibrary.TryLoad(libraryName, out IntPtr library) || !NativeLibrary.TryLoad(GlibName, out IntPtr glib))
            {
                return null;
            }

            var native = new KeyringNative(library, glib);
            return native.Probe() ? native : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or MarshalDirectiveException
                                       or BadImageFormatException)
        {
            return null;
        }
    }

    internal void Store(string account, ReadOnlySpan<byte> value)
    {
        // 값 = base64 + NUL. 고정한 배열로 넘기고 끝나면 0 으로 지운다.
        byte[] text = new byte[Base64.GetMaxEncodedToUtf8Length(value.Length) + 1];
        GCHandle pin = GCHandle.Alloc(text, GCHandleType.Pinned);
        IntPtr label = Marshal.StringToCoTaskMemUTF8(Label);
        try
        {
            Base64.EncodeToUtf8(value, text, out _, out int written);
            text[written] = 0;
            IntPtr password = pin.AddrOfPinnedObject();
            lock (_gate)
            {
                WithAttributes(account, attributes =>
                {
                    int ok = _store(Schema.Value, attributes, IntPtr.Zero, label, password, IntPtr.Zero, out IntPtr error);
                    ThrowIfFailed(ok != 0, error, "저장");
                });
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
            pin.Free();
            Marshal.FreeCoTaskMem(label);
        }
    }

    internal byte[]? Lookup(string account)
    {
        lock (_gate)
        {
            byte[]? result = null;
            WithAttributes(account, attributes =>
            {
                IntPtr found = _lookup(Schema.Value, attributes, IntPtr.Zero, out IntPtr error);
                ThrowIfFailed(error == IntPtr.Zero, error, "읽기");
                if (found != IntPtr.Zero)
                {
                    result = Decode(found);
                }
            });
            return result;
        }
    }

    internal void Clear(string account)
    {
        lock (_gate)
        {
            WithAttributes(account, attributes =>
            {
                // 없는 항목이면 false·오류 없음 → 정상.
                _clear(Schema.Value, attributes, IntPtr.Zero, out IntPtr error);
                ThrowIfFailed(error == IntPtr.Zero, error, "삭제");
            });
        }
    }

    private bool Probe()
    {
        IntPtr error = IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        WithAttributes(ProbeAccount, attributes => found = _lookup(Schema.Value, attributes, IntPtr.Zero, out error));
        if (found != IntPtr.Zero)
        {
            _passwordFree(found);
        }

        if (error != IntPtr.Zero)
        {
            _errorFree(error);
            return false;
        }

        return true;
    }

    private byte[] Decode(IntPtr found)
    {
        int length = 0;
        while (Marshal.ReadByte(found, length) != 0)
        {
            length++;
        }

        byte[] text = new byte[length];
        byte[] plain = new byte[Base64.GetMaxDecodedFromUtf8Length(length)];
        try
        {
            Marshal.Copy(found, text, 0, length);
            if (Base64.DecodeFromUtf8(text, plain, out _, out int written) != OperationStatus.Done)
            {
                throw new CryptographicException("키링 값의 형식이 맞지 않습니다.");
            }

            return plain.AsSpan(0, written).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
            CryptographicOperations.ZeroMemory(plain);
            _passwordFree(found); // libsecret 이 값을 지우고 해제한다
        }
    }

    private void WithAttributes(string account, Action<IntPtr> use)
    {
        // 키·값은 g_strdup 사본이고 표를 놓을 때 g_free 로 함께 해제된다.
        IntPtr table = _hashTableNew(_strHash, _strEqual, _free, _free);
        try
        {
            _hashTableInsert(table, _strdup("service"), _strdup(ServiceValue));
            _hashTableInsert(table, _strdup("account"), _strdup(account));
            use(table);
        }
        finally
        {
            _hashTableUnref(table);
        }
    }

    private void ThrowIfFailed(bool ok, IntPtr error, string action)
    {
        if (error != IntPtr.Zero)
        {
            // GError 의 message(키·비밀은 들어 있지 않다). domain(4)·code(4) 다음 포인터.
            string? message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8));
            _errorFree(error);
            throw new InvalidOperationException($"키링 {action}에 실패했습니다: {message}");
        }

        if (!ok)
        {
            throw new InvalidOperationException($"키링 {action}에 실패했습니다.");
        }
    }

    private static IntPtr CreateSchema()
    {
        // 프로세스가 끝날 때까지 쓰므로 해제하지 않는다.
        IntPtr schema = Marshal.AllocHGlobal(SchemaSize);
        for (int i = 0; i < SchemaSize; i++)
        {
            Marshal.WriteByte(schema, i, 0);
        }

        Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8(SchemaName));
        Marshal.WriteInt32(schema, 8, 0); // SECRET_SCHEMA_NONE
        Marshal.WriteIntPtr(schema, AttributeOffset, Marshal.StringToCoTaskMemUTF8("service"));
        Marshal.WriteInt32(schema, AttributeOffset + 8, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
        Marshal.WriteIntPtr(schema, AttributeOffset + AttributeSize, Marshal.StringToCoTaskMemUTF8("account"));
        Marshal.WriteInt32(schema, AttributeOffset + AttributeSize + 8, 0);
        return schema;
    }

    private static T Bind<T>(IntPtr library, string name)
        where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
}
