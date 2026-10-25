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
/// </summary>
public sealed partial class ServiceContainer : IServiceProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Registration>> _registrations = [];
    private readonly List<string> _deferred = [];
    private readonly List<object> _disposables = [];
    private bool _disposed;

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

    /// <summary>어셈블리의 <see cref="OrandaServiceAttribute"/> 클래스를 모두 등록한다(플랫폼 조건 적용).</summary>
    public ServiceContainer AddAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var (type, attribute) in ScanAttributes(assembly))
        {
            if (PlatformMatches(attribute.Platform))
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
    public ServiceContainer AddDeferredAssembly(string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        lock (_gate)
        {
            _deferred.Add(assemblyName);
        }

        return this;
    }

    /// <summary>지연 어셈블리를 지금 로드해 등록한다(첫 프레임 뒤 유휴 때 미리 데울 때 쓴다).</summary>
    public void ScanDeferred()
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

        foreach (var name in names)
        {
            AddAssembly(Assembly.Load(new AssemblyName(name)));
        }
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
        if (!DeferredScanned && MayBeDeferred(t))
        {
            DeferredScanTrigger ??= t.FullName;
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
