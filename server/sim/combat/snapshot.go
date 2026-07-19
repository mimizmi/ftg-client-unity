package combat

// 回滚（N5-②）的存档/还原基元：把整局模拟做深快照。回滚 = 还原到某确认帧的快照 +
// 用修正后的输入重模拟到当前帧。BattleSimulation 本就是零渲染依赖的纯类，深拷贝即快照。
//
// 深拷贝纪律：只复制【每帧可变】的状态；构建后只读的配置与招式数据按引用共享，省内存也更快。
//   · 共享（不可变）：Resolver/Pushbox/Config、moveTable、moves 映射、reactionMoveIds、
//     currentMove/reactionMove/currentMotion/airDash 指向的 MoveData、事件回调、搓招 Detector。
//   · 深拷贝（可变）：全部标量、Position/Health/状态机字段、MovementController 标量、
//     以及座位内的 Buffer/CommandQueue/inbox（经注入的 cloneSeat 完成）。
//
// 座位类型（*seat.NetworkSeat）对 combat 不可见，故深拷贝经 cloneSeat 回调注入——
// combat 不 import seat，解耦不破。

// Clone 深快照整局。cloneSeat 负责把一个座位深拷贝成新座位（回滚重放要独立的座位历史）。
// 同一次克隆里，一个 fighter 的 input 与其 Movement.input 必须指向【同一个】新座位，
// 故每个 fighter 只调 cloneSeat 一次、结果两处共用。
func (s *BattleSimulation) Clone(cloneSeat func(Seat) Seat) *BattleSimulation {
	ns := *s           // 复制全部标量 + 共享不可变指针（Resolver/Pushbox/Config/回调）
	ns.hitEvents = nil // 帧内瞬态：每次 Resolve 开头即清空，无需带走
	ns.P1 = s.P1.clone(cloneSeat(s.P1.input))
	ns.P2 = s.P2.clone(cloneSeat(s.P2.input))
	return &ns
}

// clone 深拷贝一个角色状态，把 input 与 Movement 都改指向传入的新座位。
func (f *FighterState) clone(newSeat Seat) *FighterState {
	nf := *f // 复制全部值字段 + 共享 moveTable/moves/reactionMoveIds/MoveData 指针/回调
	nf.input = newSeat
	nf.Movement = f.Movement.cloneWith(newSeat)
	return &nf
}

// cloneWith 深拷贝移动状态机，把输入视图改指向新座位（Seat 满足 MoveInput 窄接口）。
// resolveMove 闭包按引用带走：它从共享且不可变的 moves 映射取招，克隆后仍返回正确 MoveData。
func (m *MovementController) cloneWith(newInput MoveInput) *MovementController {
	nm := *m // 复制全部标量 + 共享 config/resolveMove/MoveData 指针
	nm.input = newInput
	return &nm
}
