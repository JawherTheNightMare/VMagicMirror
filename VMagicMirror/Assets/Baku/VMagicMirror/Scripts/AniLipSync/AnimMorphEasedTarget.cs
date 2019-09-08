using System.Collections.Generic;
using UnityEngine;
using VRM;

namespace Baku.VMagicMirror
{
    public class AnimMorphEasedTarget : MonoBehaviour
    {
        [Tooltip("主要な母音音素(aa, E, ih, oh, ou)に対してBlendShapeを動かすカーブ")]
        public AnimationCurve transitionCurves = new AnimationCurve(new[]
        {
            new Keyframe(0.0f, 0.0f),
            new Keyframe(0.1f, 1.0f),
        });

        [Tooltip("発音しなくなった母音のBlendShapeをゼロに近づける際の速度を表す値")]
        public float cancelSpeedFactor = 8.0f;

        [Range(0.0f, 100.0f), Tooltip("この閾値未満の音素の重みは無視する")]
        public float weightThreshold = 2.0f;

        [Tooltip("BlendShapeの値を変化させるSkinnedMeshRenderer")]
        public VRMBlendShapeProxy blendShapeProxy;

        [Tooltip("OVRLipSyncに渡すSmoothing amountの値")]
        public int smoothAmount = 100;

        //このフラグがtrueだと何も言ってないときの口の形になる。
        public bool ForceClosedMouth { get; set; }
        
        private readonly Dictionary<BlendShapeKey, float> blendShapeWeights = new Dictionary<BlendShapeKey, float>
        {
            [new BlendShapeKey(BlendShapePreset.A)] = 0.0f,
            [new BlendShapeKey(BlendShapePreset.E)] = 0.0f,
            [new BlendShapeKey(BlendShapePreset.I)] = 0.0f,
            [new BlendShapeKey(BlendShapePreset.O)] = 0.0f,
            [new BlendShapeKey(BlendShapePreset.U)] = 0.0f,
        };

        private readonly BlendShapeKey[] keys = new[]
        {
            new BlendShapeKey(BlendShapePreset.A),
            new BlendShapeKey(BlendShapePreset.E),
            new BlendShapeKey(BlendShapePreset.I),
            new BlendShapeKey(BlendShapePreset.O),
            new BlendShapeKey(BlendShapePreset.U),
        };

        OVRLipSyncContextBase context;
        OVRLipSync.Viseme _previousViseme = OVRLipSync.Viseme.sil;
        float transitionTimer = 0.0f;

        public void OnVrmLoaded(VrmLoadedInfo info)
        {
            blendShapeProxy = info.blendShape;
        }

        public void OnVrmDisposing()
        {
            blendShapeProxy = null;
        }
        
        private void Start()
        {
            context = GetComponent<OVRLipSyncContextBase>();
            if (context == null)
            {
                LogOutput.Instance.Write("同じGameObjectにOVRLipSyncContextBaseを継承したクラスが見つかりません。");
            }

            context.Smoothing = smoothAmount;
        }

        private void Update()
        {
            if (blendShapeProxy == null)
            {
                return;
            }

            //口閉じの場合: とにかく閉じるのが良いので閉じて終わり
            if (ForceClosedMouth)
            {
                UpdateToClosedMouth();
                return;
            }

            if (context == null || 
                !context.enabled || 
                !(context.GetCurrentPhonemeFrame() is OVRLipSync.Frame frame)
                )
            {
                return;
            }

            transitionTimer += Time.deltaTime;

            // 最大の重みを持つ音素を探す
            int maxVisemeIndex = 0;
            float maxVisemeWeight = 0.0f;
            // 子音は無視する
            for (var i = (int)OVRLipSync.Viseme.aa; i < frame.Visemes.Length; i++)
            {
                if (frame.Visemes[i] > maxVisemeWeight)
                {
                    maxVisemeWeight = frame.Visemes[i];
                    maxVisemeIndex = i;
                }
            }

            // 音素の重みが小さすぎる場合は口を閉じる
            if (maxVisemeWeight * 100.0f < weightThreshold)
            {
                transitionTimer = 0.0f;
            }

            // 音素の切り替わりでタイマーをリセットする
            if (_previousViseme != (OVRLipSync.Viseme)maxVisemeIndex)
            {
                transitionTimer = 0.0f;
                _previousViseme = (OVRLipSync.Viseme)maxVisemeIndex;
            }

            int visemeIndex = maxVisemeIndex - (int)OVRLipSync.Viseme.aa;
            bool hasValidMaxViseme = (visemeIndex >= 0);

            for(int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];

                blendShapeWeights[key] = Mathf.Lerp(
                    blendShapeWeights[key],
                    0.0f,
                    Time.deltaTime * cancelSpeedFactor
                    );

                //減衰中の値のほうが大きければそっちを採用する。
                //「あぁあぁぁあ」みたいな声の出し方した場合にEvaluateだけ使うとヘンテコになる可能性が高い為。
                if (hasValidMaxViseme && i == visemeIndex)
                {
                    blendShapeWeights[key] = Mathf.Max(
                        blendShapeWeights[key],
                        transitionCurves.Evaluate(transitionTimer)
                        );
                }
            }

            blendShapeProxy.SetValues(blendShapeWeights);
        }

        private void UpdateToClosedMouth()
        {
            foreach(var key in keys)
            {
                blendShapeWeights[key] = 0.0f;
            }
            blendShapeProxy.SetValues(blendShapeWeights);
        }
    }
}
