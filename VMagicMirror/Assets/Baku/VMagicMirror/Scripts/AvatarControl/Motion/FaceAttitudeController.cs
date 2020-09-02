﻿using UnityEngine;
using UniRx;
using Zenject;

namespace Baku.VMagicMirror
{
    public class FaceAttitudeController : MonoBehaviour
    {
        //NOTE: バネマス系のパラメータ(いわゆるcとk)
        [SerializeField] private Vector3 speedDumpForceFactor = new Vector3(15f, 10f, 10f);
        [SerializeField] private Vector3 posDumpForceFactor = new Vector3(50f, 50f, 50f);

        [Tooltip("NeckとHeadが有効なモデルについて、回転を最終的に振り分ける比率を指定します")]
        [Range(0f, 1f)]
        [SerializeField] private float headRate = 0.5f;

        private FaceTracker _faceTracker = null;
        private FaceRotToEuler _faceRotToEuler = null;
        
        [Inject]
        public void Initialize(FaceTracker faceTracker, OpenCVFacePose openCvFacePose, IVRMLoadable vrmLoadable)
        {
            _faceTracker = faceTracker;
            _faceRotToEuler = new FaceRotToEuler(openCvFacePose);
            
            //鏡像姿勢をベースにしたいので反転(この値を適用するとユーザーから鏡に見えるハズ)
            faceTracker.FaceParts.Outline.HeadRollRad.Subscribe(
                v => SetHeadRollDeg(-v * Mathf.Rad2Deg * HeadRollRateApplyFactor)
            );
            
            //もとの値は角度ではなく[-1, 1]の無次元量であることに注意
            faceTracker.FaceParts.Outline.HeadYawRate.Subscribe(
                v => SetHeadYawDeg(v * HeadYawRateToDegFactor)
            );

            //こっちは顔サイズで正規化された無次元量が飛んでくるので更に注意: だいたい-0.12 * 0.12くらい
            faceTracker.FaceParts.Outline.HeadPitchRate.Subscribe(
                v =>
                {
                    float rate = Mathf.Clamp(v - _faceTracker.CalibrationData.eyeFaceYDiff, -1f, 1f);
                    SetHeadPitchDeg(rate * HeadPitchRateToDegFactor);
                });
            
            vrmLoadable.VrmLoaded += info =>
            {
                var animator = info.animator;
                _neck = animator.GetBoneTransform(HumanBodyBones.Neck);
                _head = animator.GetBoneTransform(HumanBodyBones.Head);
                _hasNeck = (_neck != null);
                _hasModel = true;
            };
            
            vrmLoadable.VrmDisposing += () =>
            {
                _hasModel = false;
                _hasNeck = false;
                _neck = null;
                _head = null;
            };
        }
        
        //体の回転に反映するとかの都合で首ロールを実際に検出した値より控えめに適用しますよ、というファクター
        private const float HeadRollRateApplyFactor = 0.8f;
        //こっちの2つは角度の指定。これらの値もbodyが動くことまで加味して調整
        private const float HeadYawRateToDegFactor = 16.00f;
        private const float HeadPitchRateToDegFactor = 28.0f;
        
        private const float HeadTotalRotationLimitDeg = 40.0f;

        private const float YawSpeedToPitchDecreaseFactor = 0.01f;
        private const float YawSpeedToPitchDecreaseLimit = 10f;

        private bool _hasModel = false;
        private bool _hasNeck = false;
        private Transform _neck = null;
        private Transform _head = null;

        private void SetHeadRollDeg(float value) => _latestRotationEuler.z = value;
        private void SetHeadYawDeg(float value) => _latestRotationEuler.y = value;
        private void SetHeadPitchDeg(float value) => _latestRotationEuler.x = value;

        //NOTE: Quaternionを使わないのは角度別にローパスっぽい処理するのに都合がよいため
        private Vector3 _latestRotationEuler;
        private Vector3 _prevRotationEuler;
        private Vector3 _prevRotationSpeedEuler;

        public bool IsActive { get; set; } = true;

        private void LateUpdate()
        {
            if (!(_hasModel && _faceTracker.HasInitDone && IsActive))
            {
                _latestRotationEuler = Vector3.zero;
                _prevRotationEuler = Vector3.zero;
                _prevRotationSpeedEuler = Vector3.zero;
                return;
            }

            //従来手法
            //var target = _latestRotationEuler;
            
            //新しい手法
            var target = _faceRotToEuler.GetTargetEulerAngle();

            //やりたい事: バネマス系扱いで陽的オイラー法を回してスムージングする。
            //過減衰方向に寄せてるので雑にやっても大丈夫(のはず)
            var acceleration =
                - Mul(speedDumpForceFactor, _prevRotationSpeedEuler) -
                Mul(posDumpForceFactor, _prevRotationEuler - target);
            var speed = _prevRotationSpeedEuler + Time.deltaTime * acceleration;
            var rotationEuler = _prevRotationEuler + speed * Time.deltaTime;

            var rotationAdjusted = new Vector3(
                rotationEuler.x* PitchFactorByYaw(rotationEuler.y) + PitchDiffByYawSpeed(speed.x),
                rotationEuler.y, 
                rotationEuler.z
                );
            
            //このスクリプトより先にLookAtIKが走るハズなので、その回転と合成していく
            var rot = Quaternion.Euler(rotationAdjusted);

            //特に首と頭を一括で回すにあたって、コーナーケースを安全にするため以下のアプローチを取る
            // - 一旦今の回転値を混ぜて、
            // - 角度制限つきの最終的な回転値を作り、
            // - その回転値を角度ベースで首と頭に配り直す
            var totalRot = _hasNeck
                ? rot * _neck.localRotation * _head.localRotation
                : rot * _head.localRotation;
            
            totalRot.ToAngleAxis(
                out float totalHeadRotDeg,
                out Vector3 totalHeadRotAxis
            );
            totalHeadRotDeg = Mathf.Repeat(totalHeadRotDeg + 180f, 360f) - 180f;
            
            //素朴に値を適用すると首が曲がりすぎる、と判断されたケース
            if (Mathf.Abs(totalHeadRotDeg) > HeadTotalRotationLimitDeg)
            {
                totalHeadRotDeg = Mathf.Sign(totalHeadRotDeg) * HeadTotalRotationLimitDeg;
            }

            if (_hasNeck)
            {
                _neck.localRotation = Quaternion.AngleAxis(totalHeadRotDeg * (1 - headRate), totalHeadRotAxis);
                _head.localRotation = Quaternion.AngleAxis(totalHeadRotDeg * headRate, totalHeadRotAxis);
            }
            else
            {
                _head.localRotation = Quaternion.AngleAxis(totalHeadRotDeg, totalHeadRotAxis);
            }
            
            _prevRotationEuler = rotationEuler;
            _prevRotationSpeedEuler = speed;
        }

        //ヨーの動きがあるとき、首を下に向けさせる(首振り運動は通常ピッチが下がるのを決め打ちでやる)ための処置
        private static float PitchDiffByYawSpeed(float degPerSecond)
        {
            return Mathf.Clamp(
                degPerSecond * YawSpeedToPitchDecreaseFactor, 0, YawSpeedToPitchDecreaseLimit
                );
        }

        //ヨーが0から離れているとき、ピッチを0に近づけるための処置。
        //現行処理だとヨーが0から離れたときピッチが上に寄ってしまうので、それをキャンセルするのが狙い
        private static float PitchFactorByYaw(float yawDeg)
        {
            float rate = Mathf.Clamp01(Mathf.Abs(yawDeg / HeadYawRateToDegFactor));
            return 1.0f - rate * 0.7f;
        }
        
        private static Vector3 Mul(Vector3 left, Vector3 right) => new Vector3(
            left.x * right.x,
            left.y * right.y,
            left.z * right.z
        );
    }
    
    
    /// <summary> 頭部の回転がQuaternionで飛んで来るのを安全にオイラー角に変換してくれるやつ </summary>
    public class FaceRotToEuler
    {
        private readonly OpenCVFacePose _facePose;
        public FaceRotToEuler(OpenCVFacePose facePose)
        {
            _facePose = facePose;
        }
        
        public Vector3 GetTargetEulerAngle()
        {
            var rot = _facePose.HeadRotation;
            
            //安全にやるために、実際に基準ベクトルを回す。処理的には回転行列に置き換えるのに近いかな。
            var f = rot * Vector3.forward;
            var g = rot * Vector3.right;
            
            var yaw = Mathf.Asin(f.x) * Mathf.Rad2Deg;
            var pitch = -Mathf.Asin(f.y) * Mathf.Rad2Deg;
            var roll = Mathf.Asin(g.y) * Mathf.Rad2Deg;

            return new Vector3(
                NormalRanged(pitch),
                NormalRanged(yaw),
                NormalRanged(roll)
            );
        }
        
        //角度を必ず[-180, 180]の範囲に収めるやつ。この範囲に入ってないとスケーリングとかのときに都合が悪いため。
        private static float NormalRanged(float angle)
        {
            return Mathf.Repeat(angle + 180f, 360f) - 180f;
        }
    }
}

