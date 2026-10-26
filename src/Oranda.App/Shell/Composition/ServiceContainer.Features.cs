using System;
using System.Collections.Generic;
using Oranda.Core.Composition;
using Oranda.Core.Features;

namespace Oranda.App.Shell.Composition;

/// <summary>
/// 기능 게이트로 형 단계에서 거르는 길(TSK-07-03, TRD 10.7.2). <c>IEnumerable&lt;T&gt;</c> 해석은 모든 구현을 만들어 버려
/// 생성자 부작용(예: MapperWatchStartup → MapperWatchService)까지 도므로, 등록의 구현 형을 먼저 묻고 켜진 것만 만든다.
/// <c>GetService(IEnumerable&lt;IStartupTask&gt;)</c> 도 이 길을 탄다(시작 실행기 밖에서 시작 작업을 찾는 곳, 예: PL/SQL 작업 공간 파일 열기).
/// </summary>
public sealed partial class ServiceContainer
{
    /// <summary>
    /// 이미 등록된 서비스만 꺼낸다(없으면 null). <see cref="GetService(Type)"/> 와 달리 모르는 형이라고 지연 어셈블리를 훑지 않는다
    /// (게이트가 없는 시험 조립에서 시작 경로가 Oracle·Language 를 깨우지 않게).
    /// </summary>
    public T? GetRegistered<T>()
        where T : class
    {
        Registration? last;
        lock (_gate)
        {
            last = _registrations.TryGetValue(typeof(T), out var l) && l.Count > 0 ? l[^1] : null;
        }

        return last is null ? null : (T)Resolve(last);
    }

    /// <summary>
    /// <typeparamref name="T"/> 로 등록된 것 가운데 구현 형이 <paramref name="include"/> 를 지나는 것만 만들어 돌려준다(등록 차례).
    /// 거른 형은 만들지 않고 <paramref name="skipped"/> 로 알린다. 구현 형을 미리 모르는 팩터리 등록만은 만든 뒤 실제 형으로 묻는다.
    /// <see cref="ResolveAll"/> 처럼 지연 어셈블리를 깨우지 않는다.
    /// </summary>
    public IReadOnlyList<T> GetServices<T>(Func<Type, bool> include, Action<Type>? skipped = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(include);
        List<Registration> snapshot;
        lock (_gate)
        {
            snapshot = _registrations.TryGetValue(typeof(T), out var l) ? [.. l] : [];
        }

        var result = new List<T>(snapshot.Count);
        foreach (var r in snapshot)
        {
            if ((r.Implementation ?? r.Instance?.GetType()) is { } type)
            {
                if (include(type))
                {
                    result.Add((T)Resolve(r));
                }
                else
                {
                    skipped?.Invoke(type);
                }

                continue;
            }

            var built = (T)Resolve(r);
            if (include(built.GetType()))
            {
                result.Add(built);
            }
            else
            {
                skipped?.Invoke(built.GetType());
            }
        }

        return result;
    }

    /// <summary>
    /// 시작 작업을 기능 게이트(<see cref="IFeatureGate.IsTypeEnabled"/>, 구현 형)로 걸러 꺼낸다. 게이트가 등록돼 있지 않으면 모두.
    /// 시작 때 도는 기능(D-005: 시작 때 켬 ∧ 지금 켬)은 게이트가 답한다. 지연 어셈블리를 훑은 뒤 부르면 그 작업도 같은 조건을 탄다.
    /// </summary>
    public IReadOnlyList<IStartupTask> GetStartupTasks(Action<Type>? skipped = null)
    {
        var gate = GetRegistered<IFeatureGate>();
        return GetServices<IStartupTask>(gate is null ? static _ => true : gate.IsTypeEnabled, skipped);
    }
}

/// <summary>서비스 공급자 확장(시작 경로 보호).</summary>
public static class ServiceProviderStartupExtensions
{
    /// <summary>
    /// 이미 등록된 서비스만 꺼낸다. 컨테이너면 <see cref="ServiceContainer.GetRegistered{T}"/> 로 지연 어셈블리를 깨우지 않고,
    /// 다른 공급자(시험용)면 <c>GetService</c> 와 같다. 생성자·시작 경로에서 「있으면 따르고 없으면 말고」 할 때 쓴다.
    /// </summary>
    public static T? GetRegisteredOrDefault<T>(this IServiceProvider services)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        return services is ServiceContainer container ? container.GetRegistered<T>() : services.GetService(typeof(T)) as T;
    }
}
