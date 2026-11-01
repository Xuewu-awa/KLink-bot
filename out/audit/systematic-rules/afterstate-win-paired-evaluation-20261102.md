# Afterstate Win 配对评估

独立 afterstate 模型接入候选动作后的局面评分，Greedy 作为配对对照。4 个新种子全部通过执行健康检查，模型和对照胜负一致；本轮只做工程冒烟，未升级策略。

{
  "spec": "afterstate-win-paired-evaluation/v1",
  "modelSha256": "0f79892f6e19e0be7e5b9865b7f61834c3b2f2204266bbed71481450c512bf25",
  "dataSha256": "355ebd71834cbe39521da98d57856bf3e5ef8d2e4f778f1c8b239e6f6abbd8c8",
  "splitSha256": "8757843e5af5ae919eea5b8c8517399c677247d2613a435a101ab5673bb6f6e4",
  "evaluationPlanSha256": "d32dd9ec0016131376d67caa66cf9bb4eea0e548bc30b552ae0fa7dbd33b48b6",
  "attempts": [
    {
      "name": "eval-0.json",
      "seed": 460000,
      "matchup": [
        0,
        1
      ],
      "baseline": "Left",
      "model": "Left",
      "comparable": true,
      "delta": 0,
      "baselineSeconds": 0.8893459,
      "modelSeconds": 78.5573375,
      "modelSha256": "0f79892f6e19e0be7e5b9865b7f61834c3b2f2204266bbed71481450c512bf25",
      "planSha256": "d32dd9ec0016131376d67caa66cf9bb4eea0e548bc30b552ae0fa7dbd33b48b6"
    },
    {
      "name": "eval-1.json",
      "seed": 460001,
      "matchup": [
        1,
        0
      ],
      "baseline": "Right",
      "model": "Right",
      "comparable": true,
      "delta": 0,
      "baselineSeconds": 1.0686171,
      "modelSeconds": 5.8991361,
      "modelSha256": "0f79892f6e19e0be7e5b9865b7f61834c3b2f2204266bbed71481450c512bf25",
      "planSha256": "d32dd9ec0016131376d67caa66cf9bb4eea0e548bc30b552ae0fa7dbd33b48b6"
    },
    {
      "name": "eval-2.json",
      "seed": 460002,
      "matchup": [
        1,
        0
      ],
      "baseline": "Right",
      "model": "Right",
      "comparable": true,
      "delta": 0,
      "baselineSeconds": 0.6272951,
      "modelSeconds": 7.1275657,
      "modelSha256": "0f79892f6e19e0be7e5b9865b7f61834c3b2f2204266bbed71481450c512bf25",
      "planSha256": "d32dd9ec0016131376d67caa66cf9bb4eea0e548bc30b552ae0fa7dbd33b48b6"
    },
    {
      "name": "eval-3.json",
      "seed": 460003,
      "matchup": [
        1,
        0
      ],
      "baseline": "Right",
      "model": "Right",
      "comparable": true,
      "delta": 0,
      "baselineSeconds": 1.1623308,
      "modelSeconds": 15.3008507,
      "modelSha256": "0f79892f6e19e0be7e5b9865b7f61834c3b2f2204266bbed71481450c512bf25",
      "planSha256": "d32dd9ec0016131376d67caa66cf9bb4eea0e548bc30b552ae0fa7dbd33b48b6"
    }
  ],
  "comparableAttempts": 4,
  "modelLeftWins": 1,
  "baselineLeftWins": 1,
  "pairedDeltaSum": 0,
  "scope": "Engineering smoke only; afterstate model vs Greedy, fixed seeds, left-only, no strength claim.",
  "limitations": [
    "Four fresh attempts only; no statistical strength conclusion.",
    "The model is scored on candidate afterstates; enumeration remains structural superset and probe isolation is unchanged.",
    "Runtime hash changed since the prior model artifact, so the model was retrained with identical parameters before evaluation.",
    "No promotion to default strategy."
  ]
}
