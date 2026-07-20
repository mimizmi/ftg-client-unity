using System.Collections.Generic;

namespace Domain.Net.Transport
{
    /// <summary>
    /// 一端看到的双向输入信道：把本地输入发出去，把已到达的远端输入取回来。
    /// 回滚驱动只透过这个接口收发——于是同一套回滚逻辑，进程内可用假信道，线上用
    /// <see cref="NetClientTransport"/>（真 UDP），无缝替换。镜像 Go 侧 lockstep.Transport。
    ///
    /// Drain 一次性取走当前所有可读包（可能乱序/成批），驱动方自行按帧号归位——
    /// 这让实现可以是可靠有序的，也可以是需自行排序去重的 UDP。
    /// </summary>
    public interface ITransport
    {
        void Send(in InputPacket p);
        List<InputPacket> Drain();
    }
}
