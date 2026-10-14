using TrafficVista.Configuration;

namespace TrafficVista.Scheduling.Consumers;

/// <summary>与界面参数绑定的消费者在每轮启动前应用新的 TrafficVistaOptions。</summary>
public interface IConfigurableTaskConsumer : ITaskConsumer<Contracts.TaskEnvelope>
{
    void Configure(TrafficVistaOptions options);
}
