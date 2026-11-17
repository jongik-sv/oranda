using Oranda.App.Features.ObjectDetail;
using Oranda.App.Tests.Features.ColumnReorder;
using Oranda.Core.Features;
using Oranda.Core.Metadata;

namespace Oranda.App.Tests.Features.ObjectDetail;

// 소유: te-l1(T11b). 객체 상세 도구 줄 「열 순서 바꾸기…」 단추는 기능 columnReorder 를 따른다(스키마 브라우저 메뉴와 같게).
public sealed class ObjectDetailReorderGateTests
{
    [Fact]
    public async Task Reorder_button_is_hidden_when_the_column_reorder_feature_is_off()
    {
        var gate = new Gate { Off = FeatureIds.ColumnReorder };
        var vm = new ObjectDetailViewModel(new Services(gate));
        await vm.OpenAsync(new ReorderSession(), ReorderMeta.Emp, showDdl: false);

        Assert.Equal("열", vm.SelectedSection!.Title);
        Assert.False(vm.CanReorderColumns);
    }

    [Fact]
    public Task Reorder_button_follows_the_gate_when_it_changes() => Oranda.App.Tests.Shell.Headless.HeadlessHost.Run(async () =>
    {
        var gate = new Gate();
        var vm = new ObjectDetailViewModel(new Services(gate));
        await vm.OpenAsync(new ReorderSession(), ReorderMeta.Emp, showDdl: false);
        Assert.True(vm.CanReorderColumns); // 켜짐(지금 동작)

        gate.Off = FeatureIds.ColumnReorder;
        gate.Raise(); // Role·기능 바꾸기
        Assert.False(vm.CanReorderColumns);

        gate.Off = null;
        gate.Raise();
        Assert.True(vm.CanReorderColumns);
        vm.DetachFeatureGate();
    });

    private sealed class Gate : IFeatureGate
    {
        public string? Off { get; set; }

        public bool IsEnabled(string featureId) => featureId != Off;

        public bool IsCommandEnabled(string commandId, Type handlerType) => true;

        public bool IsPanelEnabled(string panelId, Type viewModelType) => true;

        public bool IsTypeEnabled(Type registeredType) => true;

        public bool ReadOnlyRole => false;

        public IReadOnlyCollection<string> PendingRestart { get; } = [];

        public event EventHandler? Changed;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Services(IFeatureGate gate) : IServiceProvider
    {
        private readonly ReorderMeta _meta = new();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IFeatureGate) ? gate
            : serviceType == typeof(IMetadataProvider) ? _meta
            : null;
    }
}
