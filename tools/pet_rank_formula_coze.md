# 魔力宝贝·宠物属性计算公式全解（终极版）

> 综合整理：基于魔力百科十年血瓶、華姬、荷包蛋等多篇核心文献  
> 整理日期：2026-08-01  
> **核心目标：已知档位和等级 → 算出宠物全部七维属性**

---

## 一、整体计算链路

```
档位（5项成长档）
    ↓  Step 1: 档 → BP
五项BP（体力/力量/强度/速度/魔法）
    ↓  Step 2: BP → 七维
七维属性（生命/魔力/攻击/防御/敏捷/精神/回复）
```

整个系统就两步。Step 1 因家养/野生有差异，Step 2 通用。

---

## 二、Step 2（通用）：BP → 七维换算

**无论家养、野生、BOSS，BP→七维的换算公式完全相同。**

### 2.1 自然语言

每个七维属性 = 五项BP各自乘以对应系数，求和，再加基础值。

### 2.2 系数表（宠物专用）

| BP \ 七维 | 生命 | 魔力 | 攻击 | 防御 | 敏捷 | 精神 | 回复 |
|-----------|------|------|------|------|------|------|------|
| 体力 | 8.0 | 1.0 | 0.1 | 0.1 | 0.1 | -0.3 | 0.8 |
| 力量 | 2.0 | 2.0 | 2.0 | 0.2 | 0.2 | -0.1 | -0.1 |
| 强度 | 3.0 | 2.0 | 0.2 | 3.0 | 0.2 | 0.2 | -0.1 |
| 速度 | 3.0 | 2.0 | 0.2 | 0.2 | 2.0 | -0.1 | 0.2 |
| 魔法 | 1.0 | 10.0 | 0.1 | 0.1 | 0.1 | 0.8 | -0.3 |
| **基础值** | **20** | **20** | **20** | **20** | **20** | **100** | **100** |

### 2.3 公式

```
HP    = 体BP×8.0  + 力BP×2.0  + 强BP×3.0  + 速BP×3.0  + 魔BP×1.0  + 20
MP    = 体BP×1.0  + 力BP×2.0  + 强BP×2.0  + 速BP×2.0  + 魔BP×10.0 + 20
ATK   = 体BP×0.1  + 力BP×2.0  + 强BP×0.2  + 速BP×0.2  + 魔BP×0.1  + 20
DEF   = 体BP×0.1  + 力BP×0.2  + 强BP×3.0  + 速BP×0.2  + 魔BP×0.1  + 20
AGI   = 体BP×0.1  + 力BP×0.2  + 强BP×0.2  + 速BP×2.0  + 魔BP×0.1  + 20
SPRIT = 体BP×(-0.3)+ 力BP×(-0.1)+ 强BP×0.2  + 速BP×(-0.1)+ 魔BP×0.8  + 100
REC   = 体BP×0.8  + 力BP×(-0.1)+ 强BP×(-0.1)+ 速BP×0.2  + 魔BP×(-0.3)+ 100
```

最终显示值 = **round(计算结果)**（四舍五入取整）

> 注：人物和宠物的系数不同，区别在攻击和防御（人物有装备，宠物没有，所以宠物力/强对攻防的系数更高）。

### 2.4 伪代码

```python
BP_TO_SEVEN = {
    'hp':     {'body': 8.0, 'str': 2.0, 'pow': 3.0, 'spd': 3.0, 'mag': 1.0,  'base': 20},
    'mp':     {'body': 1.0, 'str': 2.0, 'pow': 2.0, 'spd': 2.0, 'mag': 10.0, 'base': 20},
    'atk':    {'body': 0.1, 'str': 2.0, 'pow': 0.2, 'spd': 0.2, 'mag': 0.1,  'base': 20},
    'def':    {'body': 0.1, 'str': 0.2, 'pow': 3.0, 'spd': 0.2, 'mag': 0.1,  'base': 20},
    'agi':    {'body': 0.1, 'str': 0.2, 'pow': 0.2, 'spd': 2.0, 'mag': 0.1,  'base': 20},
    'spirit': {'body':-0.3, 'str':-0.1, 'pow': 0.2, 'spd':-0.1, 'mag': 0.8,  'base': 100},
    'rec':    {'body': 0.8, 'str':-0.1, 'pow':-0.1, 'spd': 0.2, 'mag':-0.3,  'base': 100},
}

def calc_seven(bp):
    """bp = {'body': x, 'str': x, 'pow': x, 'spd': x, 'mag': x}"""
    result = {}
    for stat, coeff in BP_TO_SEVEN.items():
        val = coeff['base']
        for k in ['body', 'str', 'pow', 'spd', 'mag']:
            val += bp[k] * coeff[k]
        result[stat] = round(val)
    return result
```

---

## 三、Step 1（家养宠物）：档位 → BP

### 3.1 自然语言

1. 每种宠物有5项**成长上限档**（体/力/强/速/魔），卡色决定总档上限（普卡125/银卡127/金卡129）。
2. 实际获取的宠物五项各可能**掉0~4档**，合计最多掉20档。
3. **补档**：根据卡色将总档补到上限，按体→力→强→速→魔轮流+1。
4. 补档后的档位→BP：
   - **初始BP** = 档位 × 0.2（1级时的BP）
   - **每级成长BP** = 档位 × 1/24 ≈ 档位 × 0.04167
   - **N级累计** = 档位 × [0.2 + (N-1)/24]
5. **随机档**：1级时roll一次，共10档随机分配到5项，每档=0.2BP。之后不再变化。
6. **玩家加点**：每升1级获得1个自由加点（1级时0个），加到任一属性=+1BP。有爆点规则（单项BP不能超过总BP的一半）。

### 3.2 公式

```
单项BP = 成长档 × [0.2 + (等级-1)/24] + 随机档×0.2 + 玩家加点数
```

其中：
- 成长档 = 补档后的该项档位
- 0.2 = 能力倍率20 / 100
- 1/24 ≈ 0.04167 = 每档每级BP增量
- 随机档：只在1级分配一次，之后固定

### 3.3 伪代码

```python
def calc_domestic_bp(grad, level, random_bp=0, player_alloc=0):
    """
    计算家养宠物单项BP
    grad: 补档后的成长档
    level: 宠物等级
    random_bp: 该项分到的随机档数(0~10，总和=10)
    player_alloc: 玩家加点数
    """
    INIT_FACTOR = 0.2          # 能力倍率20/100
    GROWTH_RATE = 1.0 / 24.0  # ≈0.04167
    
    base = grad * (INIT_FACTOR + (level - 1) * GROWTH_RATE)
    return base + random_bp * 0.2 + player_alloc

def fill_grads(original_grads, card_target):
    """
    补档：轮流体→力→强→速→魔循环+1
    original_grads: [体,力,强,速,魔] 原始档位
    card_target: 卡色上限 (普125/银127/金129)
    """
    FILL_ORDER = [0, 1, 2, 3, 4]  # 体→力→强→速→魔
    grads = list(original_grads)
    need = card_target - sum(grads)
    idx = 0
    for _ in range(need):
        grads[FILL_ORDER[idx % 5]] += 1
        idx += 1
    return grads

def check_burst(bp_dict, add_stat):
    """爆点检测：某项+1后是否超过总BP一半"""
    total = sum(bp_dict.values()) + 1
    return (bp_dict[add_stat] + 1) * 2 > total
```

---

## 四、Step 1（野生/BOSS宠物）：档位 → BP

### 4.1 自然语言

野生/BOSS和家养走**同一套档位体系**（每项掉0~4档，合计最多掉20档），但有三个核心区别：

1. **能力倍率不同且全程生效**：家养倍率固定20只影响1级；野生/BOSS倍率可能是30/50/100+，**每级成长都乘这个倍率**。这是BOSS属性爆炸的根本原因。
2. **随机档每级重roll**：家养只在1级分配一次；野生/BOSS每升一级都重新分配10个随机档。所以同种同级BOSS每次属性都不同。
3. **有「系」的浮动**：成长系数在0.040~0.045之间浮动（0系最强，5系最弱），而非家养的固定1/24。
4. **没有玩家加点**：野生状态下没有玩家自由分配。

### 4.2 公式

```
单项BP = (成长档 + 随机档) × [ 能力系数 × (等级-1) + 能力倍率/100 ]
```

### 4.3 参数对照

**能力系数（系）：**

| 系 | 系数 | 强弱 |
|----|------|------|
| 0系 | 0.045 | 最强 |
| 1系 | 0.044 | |
| 2系 | 0.043 | |
| 3系 | 0.042 | |
| 4系 | 0.041 | |
| 5系 | 0.040 | 最弱 |

生成公式：`系 = 5 - rand(5,10) × 0.1`，即4.0~4.5之间。

**能力倍率：**

| 类型 | 倍率 | 说明 |
|------|------|------|
| 常规宠物 | 20 | 和家养相同 |
| 使魔/小蝙蝠/牛鬼 | 30 | 1.5倍于普通 |
| 改造大炸弹 | 35 | |
| BOSS（熊男等） | **50** | 2.5倍于普通 |
| 高级BOSS（李贝留斯等） | **100+** | 5倍于普通 |

**档位生成（BOSS/野生通用）：**

```
各真实档位 = 怪物各档位极限 + rand(-4, 0)    ← 每项掉0~4档
各档位 = 各真实档位 + 随机档位               ← 10个随机档随机分配
```

### 4.4 伪代码

```python
def calc_wild_bp(grad, random_grad, level, ability_coeff, ability_rate):
    """
    计算野生/BOSS宠物单项BP
    grad: 成长档（上限-掉档）
    random_grad: 该项当前等级的随机档（每级重roll）
    level: 等级
    ability_coeff: 能力系数（0系=0.045 ... 5系=0.040）
    ability_rate: 能力倍率（常规20, 熊男50, 李贝留斯100+）
    """
    total_grad = grad + random_grad
    factor = ability_coeff * (level - 1) + ability_rate / 100.0
    return total_grad * factor

def generate_boss_grades(max_grades):
    """
    生成BOSS/野生宠物的实际档位
    max_grades: [体上限, 力上限, 强上限, 速上限, 魔上限]
    返回: [体实际, 力实际, 强实际, 速实际, 魔实际]
    """
    import random
    # 每项掉0~4档
    real_grades = [max(0, g + random.randint(-4, 0)) for g in max_grades]
    # 10个随机档随机分配
    random_grades = [0] * 5
    for _ in range(10):
        random_grades[random.randint(0, 4)] += 1
    # 最终档位 = 真实档位 + 随机档
    final_grades = [r + rnd for r, rnd in zip(real_grades, random_grades)]
    return final_grades

def calc_wild_full_stats(final_grades, level, ability_coeff, ability_rate):
    """
    计算野生/BOSS完整七维
    final_grades: 各项最终档位（含随机档）
    注意：随机档每级重roll，所以每次调用结果不同
    """
    bp = {}
    keys = ['body', 'str', 'pow', 'spd', 'mag']
    total_random = 10
    # 每级重新分配随机档
    random_alloc = distribute_random(total_random, 5)  # 随机分10档到5项
    for i, k in enumerate(keys):
        bp[k] = calc_wild_bp(final_grades[i], random_alloc[i], 
                             level, ability_coeff, ability_rate)
    return calc_seven(bp)
```

---

## 五、家养 vs 野生/BOSS 对照表

| 对比维度 | 家养宠物 | 野生/BOSS宠物 |
|----------|----------|---------------|
| **档位体系** | 相同（每项掉0~4，合计最多掉20） | 相同 |
| **能力倍率** | 固定20 | 因怪而异（20~100+） |
| **倍率作用范围** | **只影响1级** | **全程影响每级成长** |
| **成长系数** | 固定1/24≈0.04167 | 浮动0.040~0.045（系） |
| **随机档** | 1级分配一次，之后固定 | **每级重新分配** |
| **玩家加点** | 有（每级1点） | **无** |
| **同种同级差异** | 仅随机档不同（影响小） | 随机档+系都不同（差异可达几十点） |
| **捕获后** | — | 变家养规则 |

**BOSS强的核心答案：能力倍率高。** 熊男倍率50（普通2.5倍），李贝留斯100+（5倍）。不是档位高，是每级成长都乘了更高的倍率。

---

## 六、精神门槛与魔法等级

**公式：发动精神 = 103 + 魔法等级 × 20**

| 魔法等级 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | **10** |
|----------|---|---|---|---|---|---|---|---|---|--------|
| 所需精神 | 123 | 143 | 163 | 183 | 203 | 223 | 243 | 263 | 283 | **303** |

> 来源：[新浪游戏-属性攻击魔法](http://games.sina.com.cn/zhqu/cross/jnxj/shuxingmofa.shtml)、[Qi魔力-魔法精神理论](http://www.quietmoli.com/other/01/02.htm)

---

## 七、实战验证

### 7.1 星菇精神验证

**宠物数据：**
- 星菇，银卡，植物系
- 原始档位：体40 力11 强8 速11 魔37（总107）
- 银卡补档目标：127，需补20档
- 补档后：体44 力15 强12 速15 魔41（总127）

**验证1：100级满档全加魔精神**

```
factor = 0.2 + 99/24 = 4.325

体BP = 44 × 4.325 = 190.30
力BP = 15 × 4.325 = 64.875
强BP = 12 × 4.325 = 51.90
速BP = 15 × 4.325 = 64.875
魔BP = 41 × 4.325 + 99 = 276.325（99点全加魔）

精神 = 100 + 190.30×(-0.3) + 64.875×(-0.1) + 51.90×0.2 + 64.875×(-0.1) + 276.325×0.8
     = 100 - 57.09 - 6.49 + 10.38 - 6.49 + 221.06
     = 261.37 → round = 261

结论：261 < 303，到不了10级魔法线 ✓
```

**验证2：120级满档全加魔精神**

```
factor = 0.2 + 119/24 = 5.158333

体BP = 44 × 5.1583 = 226.97
力BP = 15 × 5.1583 = 77.375
强BP = 12 × 5.1583 = 61.90
速BP = 15 × 5.1583 = 77.375
魔BP = 41 × 5.1583 + 119 = 330.49（119点全加魔）

精神 = 100 + 226.97×(-0.3) + 77.375×(-0.1) + 61.90×0.2 + 77.375×(-0.1) + 330.49×0.8
     = 100 - 68.09 - 7.74 + 12.38 - 7.74 + 264.39
     = 293.21 → round = 293

结论：293 < 303，到不了10级魔法线 ✓
```

**验证3：穷举所有加点方案**

`spirit_target` 策略遍历0~119点加魔的所有可能，确认即使全加魔（精神最大化的唯一方案），精神也仅293。**任何其他加点方案精神更低。**

> **最终结论：星菇无论什么等级、什么加点方式，满档精神永远到不了303。** 极限精神出现在全加魔时，120级=293，100级=261。

### 7.2 BOSS强度验证（熊男示例）

**杀熊者殴兹那克：体82 力47 强22 速12 魔17，倍率50**

25级，能力系数4系=0.041：

```
熊男实际：体BP = 82 × (24×0.041 + 50/100) = 82 × 1.484 = 121.7
普通模拟：体BP = 82 × (24×0.0417 + 20/100) = 82 × 1.200 = 98.4
差距：121.7 vs 98.4，差23.3点BP → 生命差约 23.3×8 ≈ 186
```

这就是为什么用普通模拟器（倍率20）算出来的BOSS数据和实际完全不符。

---

## 八、完整计算器伪代码

```python
class PetStatCalculator:
    """魔力宝贝宠物属性计算器"""
    
    # ===== 常量 =====
    BP_TO_SEVEN = {...}  # 见第二章系数表
    CARD_TARGET = {'普卡': 125, '银卡': 127, '金卡': 129}
    FILL_ORDER = [0, 1, 2, 3, 4]  # 体→力→强→速→魔
    
    # ===== 家养宠物计算 =====
    def calc_domestic(self, pet_name, level, strategy):
        # 1. 获取宠物数据
        pet = self.get_pet(pet_name)
        
        # 2. 补档
        target = self.CARD_TARGET[pet.card]
        grads = self.fill_grads(pet.original_grads, target)
        
        # 3. 计算基础BP
        bp = {}
        for i, key in enumerate(['body','str','pow','spd','mag']):
            bp[key] = grads[i] * (0.2 + (level-1) / 24.0)
        
        # 4. 随机档（简化：假设均匀分配，实际可指定）
        # random_grads = [2,2,2,2,2]  # 每项2档
        # for i, key in enumerate(...):
        #     bp[key] += random_grads[i] * 0.2
        
        # 5. 玩家加点（含爆点检测）
        alloc_result = self.allocate_points(bp, level-1, strategy)
        bp = alloc_result['bp']
        
        # 6. BP → 七维
        return self.calc_seven(bp)
    
    # ===== 野生/BOSS宠物计算 =====
    def calc_wild(self, max_grades, level, ability_rate, ability_coeff=None):
        # 1. 生成实际档位（每项掉0~4 + 10随机档）
        real_grades = [max(0, g + random.randint(-4, 0)) for g in max_grades]
        random_alloc = self.distribute_random(10, 5)
        final_grades = [r + rnd for r, rnd in zip(real_grades, random_alloc)]
        
        # 2. 确定系（如无指定则随机）
        if ability_coeff is None:
            ability_coeff = 0.050 - random.randint(5, 10) * 0.01  # 0.040~0.045
        
        # 3. 计算各项BP
        bp = {}
        for i, key in enumerate(['body','str','pow','spd','mag']):
            total = final_grades[i]
            factor = ability_coeff * (level - 1) + ability_rate / 100.0
            bp[key] = total * factor
        
        # 4. BP → 七维（无玩家加点）
        return self.calc_seven(bp)
```

---

## 九、关键推论速查

| 编号 | 结论 |
|------|------|
| 1 | 档位决定上限，掉档影响有限（掉1档在160级约差1~5点七维） |
| 2 | BOSS强在倍率不在档位（熊男50=普通2.5倍，李贝留斯100+=5倍） |
| 3 | 野生同级差异大（随机档每级重roll + 系浮动，差异可达几十点） |
| 4 | 补档只补卡色差额，天生掉档不补 |
| 5 | 捕获后野生变家养（升级规则完全等同） |
| 6 | 精神门槛公式：103 + 魔法等级×20（10级=303） |
| 7 | 星菇满档精神极限：100级=261，120级=293，永远到不了303 |
| 8 | 全加魔是精神最大化的唯一方案（每加1点魔=精神+0.8，加其他都更低） |

---

## 参考来源

1. 《数据化魔力之——成长BP的深入理解》十年血瓶 | [魔力百科](https://www.molibaike.com/Article/Detail/5f964a54-b20d-4012-af81-f2a5ab933f6c)
2. 《宠物怪物BP生成能力》華姬 | [17173](https://cg.17173.com/content/09062025/090159622.shtml)
3. 《野生宠物能力构成》荷包蛋 | [Qi魔力论坛](http://bbs2.quietmoli.com/forum.php?mod=viewthread&tid=487)
4. 《野生宠物入门知识》十年血瓶 | [17173](https://cg.17173.com/content/08162025/193538006.shtml)
5. 《魔法精神理论》 | [Qi魔力](http://www.quietmoli.com/other/01/02.htm)
6. 《属性攻击魔法》 | [新浪游戏](http://games.sina.com.cn/zhqu/cross/jnxj/shuxingmofa.shtml)

---

> 本内容由 Coze AI 生成，请遵循相关法律法规及《人工智能生成合成内容标识办法》使用与传播。
