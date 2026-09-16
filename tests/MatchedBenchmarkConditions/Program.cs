using System.Runtime.CompilerServices;
using T3MPTestDriver;
using HarmonyLib;

internal static class Program
{
 static void Check(bool value) { if (!value) throw new Exception("Condition failed"); }
 static void Main(string[] args)
 {
  GC.KeepAlive(typeof(Harmony));
  var mode=args[0];
  if (mode=="bad") TestArguments.Speed=float.PositiveInfinity;
  if (mode=="missing") FixedTickBenchmark.Requested=false;
  if (mode is "bad" or "missing")
  {
   try { MatchedBenchmarkConditions.Install(); throw new Exception("Accepted invalid arguments"); }
   catch (ArgumentException) { Console.WriteLine("PASS invalid input"); return; }
  }
  MatchedBenchmarkConditions.Install();
  Check(MatchedBenchmarkConditions.Installed==(mode=="on"));
  var speed=new Timberborn.TimeSystem.SpeedManager();
  Check(speed.Read(0)==0);
  Check(speed.Read(-1)==-1);
  Check(float.IsNaN(speed.Read(float.NaN)));
  Check(float.IsPositiveInfinity(speed.Read(float.PositiveInfinity)));
  Check(speed.Read(7)==(mode=="on"?50:3.4f));
  Check(speed.Calls==5); // Original ScaleSpeed still executes on every request.
  UnityEngine.Time.timeScale=50;
  MatchedBenchmarkConditions.PrepareTiming();
  Check(UnityEngine.QualitySettings.vSyncCount==(mode=="on"?0:1));
  Check(UnityEngine.Application.targetFrameRate==(mode=="on"?-1:60));
  Check(MatchedBenchmarkConditions.LegacyState()=="absent");
  if (mode=="on")
  {
   UnityEngine.Time.timeScale=7;
   try { MatchedBenchmarkConditions.PrepareTiming(); throw new Exception("Accepted unmatched speed"); }
   catch (InvalidOperationException) { }
  }
  Console.WriteLine("PASS common speed control, native execution, pause/invalid inputs, frame setup, absent legacy state");
 }
}
namespace T3MPTestDriver
{
 internal static class TestArguments { internal static float? Speed=50; }
 internal static class FixedTickBenchmark { internal static bool Requested=true; }
}
namespace Timberborn.TimeSystem
{
 public sealed class SpeedManager
 {
  public int Calls;
  [MethodImpl(MethodImplOptions.NoInlining)] private float ScaleSpeed(float speed) { Calls++; return speed>1?1+(speed-1)*0.4f:speed; }
  public float Read(float speed) => ScaleSpeed(speed);
 }
}
namespace UnityEngine
{
 public static class Debug { public static void Log(object text) => Console.WriteLine(text); }
 public static class Time { public static float timeScale; }
 public static class QualitySettings { public static int vSyncCount=1; }
 public static class Application { public static int targetFrameRate=60; }
}
