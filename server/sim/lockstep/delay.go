package lockstep

import "math"

// DelayController 依据实测 RTT（帧）推荐最优【输入延迟 D】。回滚下的核心权衡：
//
//	· D=0：本地输入零延迟，但远端输入常来不及 → 预测失误多 → 回滚重模拟（画面回跳）频繁。
//	· D≈单程延迟：本地输入延迟 D 帧生效，但远端输入大多准时到达 → 预测命中 → 回滚罕见。
//
// 目标 D ≈ 单程延迟 = RTT/2：既压住绝大多数回滚，又只引入最小输入延迟。SF6/GGPO 家族的
// 「输入延迟 + 回滚」混合正是这个思路。
//
// 【确定性纪律】D 决定本端输入落在哪个 sim 帧、且随包传输给对端，故【开局或局间】按 RTT 选定即可，
// 两端各自选各自的 D 不破坏确定性（帧号已随输入过线）。但【局中】更改 D 会移动 primer 延迟窗口约定，
// 必须两端在约定帧同步切换——本控制器只做推荐，是否/何时采用交由上层（服务器可权威下发）。
//
// 抗抖：EWMA 平滑本身即是迟滞——单帧的 RTT 尖刺被指数平均吸收，推荐 D 不会逐样本来回跳；
// 持续的延迟变化才平滑地带动 D。取 round（而非 ceil）避免抖动下的单向抬升；偶发欠估只会引发
// 一次可自愈的回滚，系统本就容得下。
type DelayController struct {
	minDelay, maxDelay int
	alpha              float64 // EWMA 平滑系数（越小越稳、越大越跟手）
	smoothedOneWay     float64 // 单程延迟（帧）的指数滑动平均
	initialized        bool
	current            int
}

// NewDelayController 创建控制器，推荐 D 夹在 [minDelay, maxDelay]。maxDelay ≤ minDelay 时取 minDelay。
func NewDelayController(minDelay, maxDelay int) *DelayController {
	if minDelay < 0 {
		minDelay = 0
	}
	if maxDelay < minDelay {
		maxDelay = minDelay
	}
	return &DelayController{minDelay: minDelay, maxDelay: maxDelay, alpha: 0.15, current: minDelay}
}

// Observe 吞入一个 RTT（帧）观测，更新平滑估计并返回当前推荐 D。
func (c *DelayController) Observe(rttFrames int) int {
	if rttFrames < 0 {
		rttFrames = 0
	}
	oneWay := float64(rttFrames) / 2
	if !c.initialized {
		c.smoothedOneWay = oneWay
		c.initialized = true
	} else {
		c.smoothedOneWay = c.alpha*oneWay + (1-c.alpha)*c.smoothedOneWay
	}

	target := min(max(int(math.Round(c.smoothedOneWay)), c.minDelay), c.maxDelay)
	c.current = target
	return c.current
}

// Recommended 返回当前推荐 D（不吞新观测）。
func (c *DelayController) Recommended() int { return c.current }
