﻿using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace TS.NET;

public class RisingEdgeTriggerI8 : ITriggerI8
{
    enum TriggerState { Unarmed, Armed, InCapture, InHoldoff }
    private TriggerState triggerState = TriggerState.Unarmed;

    private sbyte triggerLevel;
    private sbyte armLevel;

    private long captureSamples;
    private long captureRemaining;

    private long holdoffSamples;
    private long holdoffRemaining;

    public RisingEdgeTriggerI8(EdgeTriggerParameters parameters)
    {
        SetParameters(parameters);
        SetHorizontal(1000000, 0, 0);
    }

    public void SetParameters(EdgeTriggerParameters parameters)
    {
        if (parameters.Level == sbyte.MinValue)
            parameters.Level += (sbyte)parameters.Hysteresis;  // Coerce so that the trigger arm level is sbyte.MinValue, ensuring a non-zero chance of seeing some waveforms
        if (parameters.Level == sbyte.MaxValue)
            parameters.Level -= 1;                  // Coerce as the trigger logic is GT, ensuring a non-zero chance of seeing some waveforms

        triggerState = TriggerState.Unarmed;
        triggerLevel = (sbyte)parameters.Level;
        armLevel = (sbyte)parameters.Level;
        armLevel -= (sbyte)parameters.Hysteresis;
    }

    public void SetHorizontal(long windowWidth, long windowTriggerPosition, long additionalHoldoff)
    {
        if (windowWidth < 1000)
            throw new ArgumentException($"windowWidth cannot be less than 1000");
        if (windowTriggerPosition > (windowWidth - 1))
            windowTriggerPosition = windowWidth - 1;

        triggerState = TriggerState.Unarmed;

        captureSamples = windowWidth - windowTriggerPosition;
        captureRemaining = 0;

        holdoffSamples = windowWidth - captureSamples + additionalHoldoff;
        holdoffRemaining = 0;
    }

    //[MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Process(ReadOnlySpan<sbyte> input, Span<int> windowEndIndices, out int windowEndCount)
    {
        int inputLength = input.Length;
        int simdLength = inputLength - 32;
        windowEndCount = 0;
        int i = 0;

        Vector256<sbyte> triggerLevelVector256 = Vector256.Create(triggerLevel);
        Vector256<sbyte> armLevelVector256 = Vector256.Create(armLevel);
        Vector128<sbyte> triggerLevelVector128 = Vector128.Create(triggerLevel);
        Vector128<sbyte> armLevelVector128 = Vector128.Create(armLevel);

        windowEndIndices.Clear();
        unsafe
        {
            fixed (sbyte* samplesPtr = input)
            {
                while (i < inputLength)
                {
                    switch (triggerState)
                    {
                        case TriggerState.Unarmed:
                            if (Avx2.IsSupported)       // Const after JIT/AOT
                            {
                                while (i < simdLength)
                                {
                                    var inputVector = Avx.LoadVector256(samplesPtr + i);
                                    var resultVector = Avx2.CompareEqual(Avx2.Max(armLevelVector256, inputVector), armLevelVector256);
                                    var conditionFound = Avx2.MoveMask(resultVector) != 0;     // Quick way to do horizontal vector scan of byte[n] > 0
                                    if (conditionFound)     // Alternatively, use BitOperations.TrailingZeroCount and add the offset
                                        break;
                                    i += 32;
                                }
                            }
                            else if (AdvSimd.Arm64.IsSupported)
                            {
                                while (i < simdLength)
                                {
                                    var inputVector1 = AdvSimd.LoadVector128(samplesPtr + i);
                                    var inputVector2 = AdvSimd.LoadVector128(samplesPtr + i + 16);
                                    var resultVector1 = AdvSimd.CompareLessThanOrEqual(inputVector1, armLevelVector128);
                                    var resultVector2 = AdvSimd.CompareLessThanOrEqual(inputVector2, armLevelVector128);
                                    var conditionFound = resultVector1 != Vector128<sbyte>.Zero;
                                    conditionFound |= resultVector2 != Vector128<sbyte>.Zero;
                                    if (conditionFound)
                                        break;
                                    i += 32;

                                    // https://branchfree.org/2019/04/01/fitting-my-head-through-the-arm-holes-or-two-sequences-to-substitute-for-the-missing-pmovmskb-instruction-on-arm-neon/
                                    // var inputVector = AdvSimd.Arm64.Load4xVector128AndUnzip(samplesPtr + i);
                                    // var resultVector1 = AdvSimd.CompareLessThanOrEqual(inputVector.Value1, armLevelVector128);
                                    // var resultVector2 = AdvSimd.CompareLessThanOrEqual(inputVector.Value2, armLevelVector128);
                                    // var resultVector3 = AdvSimd.CompareLessThanOrEqual(inputVector.Value3, armLevelVector128);
                                    // var resultVector4 = AdvSimd.CompareLessThanOrEqual(inputVector.Value4, armLevelVector128);
                                    // var t0 = AdvSimd.ShiftRightAndInsert(resultVector2, resultVector1, 1);
                                    // var t1 = AdvSimd.ShiftRightAndInsert(resultVector4, resultVector3, 1);
                                    // var t2 = AdvSimd.ShiftRightAndInsert(t1,t0, 2);
                                    // var t3 = AdvSimd.ShiftRightAndInsert(t2,t2, 4);
                                    // var t4 = AdvSimd.ShiftRightLogicalNarrowingLower(t3.AsUInt16(), 4);
                                    // var result = t4.AsUInt64()[0];
                                    // if(result != 0)
                                    // {
                                    //     var offset = BitOperations.TrailingZeroCount(result);
                                    //     i += (uint)offset;
                                    //     break;
                                    // }
                                    // i += 64;

                                    // var inputVector = AdvSimd.Arm64.Load4xVector128(samplesPtr + i);
                                    // var resultVector1 = AdvSimd.CompareLessThanOrEqual(inputVector.Value1, armLevelVector128);
                                    // var resultVector2 = AdvSimd.CompareLessThanOrEqual(inputVector.Value2, armLevelVector128);
                                    // var resultVector3 = AdvSimd.CompareLessThanOrEqual(inputVector.Value3, armLevelVector128);
                                    // var resultVector4 = AdvSimd.CompareLessThanOrEqual(inputVector.Value4, armLevelVector128);
                                    // var conditionFound = resultVector1 != Vector128<sbyte>.Zero;
                                    // conditionFound |= resultVector2 != Vector128<sbyte>.Zero;
                                    // conditionFound |= resultVector3 != Vector128<sbyte>.Zero;
                                    // conditionFound |= resultVector4 != Vector128<sbyte>.Zero;
                                    // if (conditionFound)
                                    //     break;
                                    // i += 64;
                                }
                            }
                            while (i < inputLength)
                            {
                                if (samplesPtr[i] <= armLevel)
                                {
                                    triggerState = TriggerState.Armed;
                                    break;
                                }
                                i++;
                            }
                            break;
                        case TriggerState.Armed:
                            if (Avx2.IsSupported)       // Const after JIT/AOT
                            {
                                while (i < simdLength)
                                {
                                    var inputVector = Avx.LoadVector256(samplesPtr + i);
                                    var resultVector = Avx2.CompareEqual(Avx2.Min(triggerLevelVector256, inputVector), triggerLevelVector256);
                                    var conditionFound = Avx2.MoveMask(resultVector) != 0;     // Quick way to do horizontal vector scan of byte[n] != 0
                                    if (conditionFound)     // Alternatively, use BitOperations.TrailingZeroCount and add the offset
                                        break;
                                    i += 32;
                                }
                            }
                            else if (AdvSimd.Arm64.IsSupported)
                            {
                                while (i < simdLength)
                                {
                                    var inputVector1 = AdvSimd.LoadVector128(samplesPtr + i);
                                    var inputVector2 = AdvSimd.LoadVector128(samplesPtr + i + 16);
                                    var resultVector1 = AdvSimd.CompareGreaterThan(inputVector1, triggerLevelVector128);
                                    var resultVector2 = AdvSimd.CompareGreaterThan(inputVector2, triggerLevelVector128);
                                    var conditionFound = resultVector1 != Vector128<sbyte>.Zero;
                                    conditionFound |= resultVector2 != Vector128<sbyte>.Zero;
                                    if (conditionFound)
                                        break;
                                    i += 32;
                                }
                            }
                            while (i < inputLength)
                            {
                                if (samplesPtr[i] > triggerLevel)
                                {
                                    triggerState = TriggerState.InCapture;
                                    captureRemaining = captureSamples;
                                    break;
                                }
                                i++;
                            }
                            break;
                        case TriggerState.InCapture:
                            {
                                int remainingSamples = inputLength - i;
                                if (remainingSamples > captureRemaining)
                                {
                                    i += (int)captureRemaining;    // Cast is ok because remainingSamples (in the conditional expression) is uint
                                    captureRemaining = 0;
                                }
                                else
                                {
                                    captureRemaining -= remainingSamples;
                                    i = inputLength;    // Ends the state machine loop
                                }
                                if (captureRemaining == 0)
                                {
                                    windowEndIndices[windowEndCount++] = i;
                                    if (holdoffSamples > 0)
                                    {
                                        triggerState = TriggerState.InHoldoff;
                                        holdoffRemaining = holdoffSamples;
                                    }
                                    else
                                    {
                                        triggerState = TriggerState.Unarmed;
                                    }
                                }
                            }
                            break;
                        case TriggerState.InHoldoff:
                            {
                                int remainingSamples = inputLength - i;
                                if (remainingSamples > holdoffRemaining)
                                {
                                    i += (int)holdoffRemaining;    // Cast is ok because remainingSamples (in the conditional expression) is uint
                                    holdoffRemaining = 0;
                                }
                                else
                                {
                                    holdoffRemaining -= remainingSamples;
                                    i = inputLength;    // Ends the state machine loop
                                }
                                if (holdoffRemaining == 0)
                                {
                                    triggerState = TriggerState.Unarmed;
                                }
                            }
                            break;
                    }
                }
            }
        }
    }
}