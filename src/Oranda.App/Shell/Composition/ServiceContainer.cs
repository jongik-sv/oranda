using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Oranda.Core.Composition;

namespace Oranda.App.Shell.Composition;

/// <summary>
/// 가벼운 서비스 컨테이너(생성자 주입). <see cref="OrandaServiceAttribute"/> 를 단 클래스를 어셈블리 단위로 모아 등록한다.
/// 외부 DI 패키지 없이 두 가지만 더 한다: <see cref="AddDeferredAssembly"/>(처음 모르는 형식을 물을 때 비로소 훑는다 —
/// 시작 경로에서 Oracle·ANTLR 어셈블리를 로드하지 않기 위함, TRD 8)와 <c>IEnumerable&lt;T&gt;</c> 해석.
/// DBMS 키(<see cref="OrandaServiceAttribute.ForDbms"/>)를 단 등록은 서비스 형식으로 바로 등록하지 않고 <see cref="DbmsServices"/> 에 넣는다(D-046) —
/// 앱이 보는 서비스 형식 자리는 Core 디스패처(<c>Oranda.Core.Composition.Dispatch</c>)가 맡아 세션의 DBMS 로 고른다.
/// </summary>
public sealed partial class ServiceContainer : IServiceProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly object _scanGate = new();
    private readonly Dictionary<Type, List<Registration>> _registrations = [];
    private readonly List<string> _deferred = [];

    /// <summary>없어도 되는 지연 어셈블리(예: 드라이버 없는 배포의 Oranda.Tibero). 못 읽으면 건너뛰고 다른 어셈블리 등록을 막지 않는다.</summary>
    private readonly HashSet<string> _optionalDeferred = new(StringComparer.Ordinal);
    private readonly List<object> _disposables = [];
    private readonly List<Action> _afterScan = [];
    private bool _disposed;
    private volatile bool _scanDone;

    public ServiceContainer()
    {
        // 표가 답하기 전에 지연 어셈블리를 훑는다: 디스패처를 꺼내는 것만으로는 Oracle 을 로드하지 않고, 처음 구현을 고를 때 로드한다.
        DbmsServices = new DbmsServiceTable(ensureLoaded: () =>
        {
            if (!DeferredScanned)
            {
                DeferredScanTrigger ??= typeof(IDbmsServiceSelector).FullName;
            }

            ScanDeferred();
        });
        AddInstance<IDbmsServiceSelector>(DbmsServices);
    }

    /// <summary>(서비스 형식, DBMS) → 구현 표. 디스패처가 <see cref="IDbmsServiceSelector"/> 로 받는다.</summary>
    public DbmsServiceTable DbmsServices { get; }

    /// <summary>지연 어셈블리를 지금까지 훑었는지(시험·진단용).</summary>
    public bool DeferredScanned { get; private set; }

    /// <summary>지연 어셈블리 훑기를 일으킨 형식 이름(진단용). 훑지 않았으면 null.</summary>
    public string? DeferredScanTrigger { get; private set; }

    /// <summary>이미 만든 인스턴스를 싱글턴으로 등록한다.</summary>
    public ServiceContainer AddInstance(Type serviceType, object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Add(serviceType, new Registration(serviceType, instance.GetType(), ServiceLifetimeKind.Singleton, null) { Instance = instance });
        return this;
    }

    public ServiceContainer AddInstance<T>(T instance)
        where T : class => AddInstance(typeof(T), instance);

    /// <summary>형식을 만드는 방법을 직접 준다.</summary>
    public ServiceContainer AddFactory(Type serviceType, Func<ServiceContainer, object> factory, ServiceLifetimeKind lifetime = ServiceLifetimeKind.Singleton)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Add(serviceType, new Registration(serviceType, null, lifetime, factory));
        return this;
    }

    public ServiceContainer AddType(Type serviceType, Type implementation, ServiceLifetimeKind lifetime = ServiceLifetimeKind.Singleton)
    {
        Add(serviceType, new Registration(serviceType, implementation, lifetime, null));
        return this;
    }

    /// <summary>
    /// 어셈블리의 <see cref="OrandaServiceAttribute"/> 클래스를 모두 등록한다(플랫폼 조건 적용).
    /// DBMS 키를 단 것은 <see cref="DbmsServices"/> 에 (서비스 형식, DBMS) 로 넣고 서비스 형식으로는 등록하지 않는다(수명은 특성 그대로).
    /// </summary>
    public ServiceContainer AddAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var (type, attribute) in ScanAttributes(assembly))
        {
            if (!PlatformMatches(attribute.Platform))
            {
                continue;
            }

            if (attribute.ForDbms is { } dbms)
            {
                var implementation = type;
                DbmsServices.Add(attribute.ServiceType, dbms, () => Create(implementation), attribute.Lifetime);
            }
            else
            {
                Add(attribute.ServiceType, new Registration(attribute.ServiceType, type, attribute.Lifetime, null));
            }
        }

        return this;
    }

    /// <summary>
    /// 어셈블리 이름만 적어 두고, 컨테이너가 모르는 형식을 처음 받을 때 로드해 등록한다.
    /// 이미 로드된 어셈블리를 다시 훑지 않는다.
    /// </summary>
    public ServiceContainer AddDeferredAssembly(string assemblyName, bool optional = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        lock (_gate)
        {
            _deferred.Add(assemblyName);
            if (optional)
            {
                _optionalDeferred.Add(assemblyName);
            }
        }

        return this;
    }

    /// <summary>
    /// 지연 어셈블리를 지금 로드해 등록한다(첫 프레임 뒤 유휴 때 미리 데울 때 쓴다). 두 번째부터는 아무것도 하지 않는다.
    /// <see cref="DbmsServices"/> 가 답하기 전에도 불린다. 다른 스레드가 훑는 중이면 끝날 때까지 기다린다(덜 채운 표로 답하지 않게).
    /// 같은 스레드에서 다시 들어오면(훑는 중 등록이 다시 묻는 경우) 이미 표시했으므로 바로 돌아간다.
    /// </summary>
    public void ScanDeferred()
    {
        if (_scanDone)
        {
            return; // DBMS 구현을 고를 때마다 불리므로 다 훑은 뒤에는 잠그지 않는다
        }

        lock (_scanGate)
        {
            string[] names;
            lock (_gate)
            {
                if (DeferredScanned)
                {
                    return;
                }

                DeferredScanned = true;
                names = [.. _deferred];
            }

            // 훑기는 등록만 한다(인스턴스를 만들지 않는다) — 바깥 잠금(_scanGate)을 쥔 채 남의 잠금을 기다리지 않는다.
            Action[] after;
            try
            {
                foreach (var name in names)
                {
                    Assembly loaded;
                    try
                    {
                        loaded = Assembly.Load(new AssemblyName(name));
                    }
                    catch (Exception ex) when ((ex is FileNotFoundException or FileLoadException or BadImageFormatException) && IsOptionalDeferred(name))
                    {
                        continue; // 선택 어셈블리가 없다 — 그 DBMS 만 빠지고 Oracle·Language 는 그대로 등록한다.
                    }

                    AddAssembly(loaded);
                }
            }
            finally
            {
                lock (_gate)
                {
                    after = [.. _afterScan];
                    _afterScan.Clear();
                }

                _scanDone = true;
            }

            foreach (var action in after)
            {
                action();
            }
        }
    }

    private bool IsOptionalDeferred(string name)
    {
        lock (_gate)
        {
            return _optionalDeferred.Contains(name);
        }
    }

    /// <summary>
    /// 지연 어셈블리를 다 훑은 뒤 한 번 부를 일을 건다(이미 훑었으면 바로 부른다).
    /// 지연 어셈블리 안의 정적 상태를 잇는 데 쓴다 — 시작 경로에서 그 어셈블리를 로드하지 않게(TRD 8).
    /// </summary>
    public ServiceContainer AfterDeferredScan(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (!DeferredScanned)
            {
                _afterScan.Add(action);
                return this;
            }
        }

        // 훑는 중이면 끝날 때까지 기다린 뒤 부른다
        ScanDeferred();
        action();
        return this;
    }

    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(ServiceContainer))
        {
            return this;
        }

        if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            // 시작 작업 목록은 누가 꺼내도 기능 게이트로 거른 것이다(꺼진 기능의 작업을 만들지 않는다, TSK-07-03)
            var element = serviceType.GetGenericArguments()[0];
            return element == typeof(IStartupTask) ? GetStartupTasks().ToArray() : ResolveAll(element);
        }

        if (TryGetRegistrations(serviceType, out var list))
        {
            return Resolve(list[^1]);
        }

        return null;
    }

    public T? GetService<T>()
        where T : class => (T?)GetService(typeof(T));

    public T GetRequiredService<T>()
        where T : class => (T)GetRequiredService(typeof(T));

    public object GetRequiredService(Type serviceType) =>
        GetService(serviceType) ?? throw new InvalidOperationException($"서비스 '{serviceType.FullName}' 가 등록되어 있지 않다.");

    /// <summary>등록과 상관없이 생성자 주입으로 새로 만든다(명령 처리기·도구 창 ViewModel).</summary>
    public object Create(Type implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        var ctor = implementation
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => !c.IsStatic)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault(c => c.GetParameters().All(CanSatisfy))
            ?? throw new InvalidOperationException($"'{implementation.FullName}' 의 생성자 인자를 모두 해석할 수 없다.");

        var args = ctor.GetParameters()
            .Select(p => (p.ParameterType.IsValueType || p.ParameterType == typeof(string) ? null : GetService(p.ParameterType))
                ?? (p.HasDefaultValue ? p.DefaultValue : null))
            .ToArray();
        var instance = ctor.Invoke(args);
        if (instance is IDisposable or IAsyncDisposable)
        {
            lock (_gate)
            {
                _disposables.Add(instance);
            }
        }

        return instance;
    }

    public void Dispose()
    {
        object[] toDispose;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toDispose = [.. _disposables];
        }

        foreach (var d in toDispose.Reverse())
        {
            (d as IDisposable)?.Dispose();
        }
    }

    private static IEnumerable<(Type Type, OrandaServiceAttribute Attribute)> ScanAttributes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = [.. e.Types.Where(t => t is not null)!];
        }

        foreach (var type in types)
        {
            foreach (var a in type.GetCustomAttributes<OrandaServiceAttribute>(inherit: false))
            {
                yield return (type, a);
            }
        }
    }

    private static bool PlatformMatches(ServicePlatform platform) => platform switch
    {
        ServicePlatform.Windows => OperatingSystem.IsWindows(),
        ServicePlatform.MacOS => OperatingSystem.IsMacOS(),
        ServicePlatform.Other => !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS(),
        _ => true,
    };

    private void Add(Type serviceType, Registration registration)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue(serviceType, out var list))
            {
                _registrations[serviceType] = list = [];
            }

            list.Add(registration);
        }
    }

    private bool TryGetRegistrations(Type serviceType, out List<Registration> list)
    {
        lock (_gate)
        {
            if (_registrations.TryGetValue(serviceType, out var found) && found.Count > 0)
            {
                list = found;
                return true;
            }
        }

        if (MayBeDeferred(serviceType))
        {
            DeferredScanTrigger ??= serviceType.FullName;
            ScanDeferred();
            lock (_gate)
            {
                if (_registrations.TryGetValue(serviceType, out var found) && found.Count > 0)
                {
                    list = found;
                    return true;
                }
            }
        }

        list = [];
        return false;
    }

    // 프레임워크 형식(예: TimeProvider)은 지연 어셈블리가 등록하지 않으므로 훑지 않는다.
    private static bool MayBeDeferred(Type t) => t.Namespace?.StartsWith("System", StringComparison.Ordinal) != true;

    private bool CanSatisfy(ParameterInfo p)
    {
        var t = p.ParameterType;
        if (t.IsValueType || t == typeof(string))
        {
            return p.HasDefaultValue;
        }

        if (t == typeof(IServiceProvider) || t == typeof(ServiceContainer))
        {
            return true;
        }

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return true;
        }

        lock (_gate)
        {
            if (_registrations.TryGetValue(t, out var l) && l.Count > 0)
            {
                return true;
            }
        }

        // 지연 어셈블리 안 형식일 수 있으니 훑은 뒤 다시 본다(이미 훑었으면 값이 바뀌지 않는다).
        // DeferredScanned 는 훑기 시작 때 켜지므로 조건으로 쓰지 않는다 — 다른 스레드가 훑는 중이면 ScanDeferred 가 끝날 때까지 기다린다.
        if (MayBeDeferred(t))
        {
            if (!DeferredScanned)
            {
                DeferredScanTrigger ??= t.FullName;
            }

            ScanDeferred();
            lock (_gate)
            {
                if (_registrations.TryGetValue(t, out var l) && l.Count > 0)
                {
                    return true;
                }
            }
        }

        return p.HasDefaultValue;
    }

    private Array ResolveAll(Type elementType)
    {
        // 목록형(변환기 등)은 없으면 빈 목록이다. 지연 어셈블리를 깨우지 않는다(시작 경로 보호).
        List<Registration> snapshot;
        lock (_gate)
        {
            snapshot = _registrations.TryGetValue(elementType, out var l) ? [.. l] : [];
        }

        var array = Array.CreateInstance(elementType, snapshot.Count);
        for (var i = 0; i < snapshot.Count; i++)
        {
            array.SetValue(Resolve(snapshot[i]), i);
        }

        return array;
    }

    private object Resolve(Registration r)
    {
        if (r.Lifetime == ServiceLifetimeKind.Transient)
        {
            return Build(r);
        }

        lock (r)
        {
            return r.Instance ??= Build(r);
        }
    }

    private object Build(Registration r)
    {
        if (r.Factory is not null)
        {
            return r.Factory(this);
        }

        if (r.Instance is not null)
        {
            return r.Instance;
        }

        return Create(r.Implementation!);
    }

    private sealed class Registration(Type serviceType, Type? implementation, ServiceLifetimeKind lifetime, Func<ServiceContainer, object>? factory)
    {
        public Type ServiceType { get; } = serviceType;

        public Type? Implementation { get; } = implementation;

        public ServiceLifetimeKind Lifetime { get; } = lifetime;

        public Func<ServiceContainer, object>? Factory { get; } = factory;

        public object? Instance { get; set; }
    }
}
