using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using UniStorm;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Native effect/dispatcher probes in two isolated game processes. This suite
    // does not pretend that calling an effect method tests its item/seat input path.
    internal sealed class PairedAnxietyTest : IDisposable
    {
        private const byte Marker = 0xa9;
        private const float Baseline = .65f, Tolerance = .00002f;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly BasicNeeds_Manager needs;
        private readonly WorldChannel channel;
        private readonly string table;
        private readonly FieldInfo[] fields;
        private readonly object[] original;
        private readonly UniStormSystem.EnableFeature flow;
        private readonly UnityEngine.PostProcessing.VignetteModel.Settings vignette;
        private int index, stage, seeded = -1;
        private float hostAfter, guestAfter, nextSend, start;
        private byte[]? lastAck;
        private string? failure;
        private bool disposed;
        private static readonly string[] Cases = { "idle_30_minutes", "sleeping_30_minutes", "computer_self_30_minutes",
            "computer_other_30_minutes", "smoking_5_seconds", "smoking_10_seconds", "maple_wine_25_percent", "bar_drink_15_percent",
            "pet_Leo", "derby_force_2", "derby_force_8", "vehicle_relief_root", "refill_lifecycle" };
        private static readonly float[] Expected = { Baseline+30*.00020833334f, Baseline, Baseline-30*.0029166667f,
            Baseline-30*.0029166667f, .40f, .15f, .40f, .50f, .40f, .60f, .50f, .64f, 0 };
        public string Status => "anxiety host/client comparison " + index + "/" + Cases.Length + " stage " + stage;

        internal PairedAnxietyTest(MonMPPlugin plugin, Gameplay gameplay, string resultPath)
        {
            Require(LocalTestIsolation.Installed && plugin.TestOptions.Enabled, "Isolated development fixture required.");
            this.plugin=plugin; this.gameplay=gameplay; needs=gameplay.GetComponent<BasicNeeds_Manager>();
            Require(gameplay.Get_PlayerState==PlayerState.Walking && !needs.Dead && !needs.IsInvoking("Alchool_FinishedDrinking"),
                "Start walking, alive, with no pending drink callback.");
            channel=plugin.World!.Channel; flow=UniStormSystem.Instance.TimeFlow;
            fields=typeof(BasicNeeds_Manager).GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)
                .Where(f=>!f.IsInitOnly && (f.FieldType==typeof(float)||f.FieldType==typeof(int)||f.FieldType==typeof(bool))).ToArray();
            original=Capture(); vignette=Profile().vignette.settings;
            table=Path.Combine(Path.GetDirectoryName(resultPath)!,"anxiety-comparison.tsv");
            File.WriteAllText(table,"case\thost_before\thost_after\tclient_before\tclient_after\texpected_after\tpersonal_isolation\n");
            if(plugin.TestOptions.IsHost) channel.CommandRequested+=Command;
            else channel.ReplyReceived+=Reply;
            start=Time.unscaledTime;
        }

        private UnityEngine.PostProcessing.PostProcessingProfile Profile() =>
            (UnityEngine.PostProcessing.PostProcessingProfile)AccessTools.Field(typeof(BasicNeeds_Manager),"PostProcessProfile").GetValue(needs);
        private object[] Capture()=>fields.Select(f=>f.GetValue(needs)).ToArray();
        private void Restore(object[] values) { for(int i=0;i<fields.Length;i++) fields[i].SetValue(needs,values[i]); }
        private static void Require(bool value,string reason) { if(!value) throw new InvalidOperationException("Anxiety comparison: "+reason); }
        private static void Near(float actual,float expected,string reason)=>Require(!float.IsNaN(actual)&&Math.Abs(actual-expected)<=Tolerance,reason+": "+actual+" vs "+expected);
        private byte[] Packet(byte kind,int number,params float[] values)
        {
            using(var stream=new MemoryStream()) using(var writer=new BinaryWriter(stream))
            { writer.Write(Marker); writer.Write(kind); writer.Write(number); foreach(float value in values) writer.Write(value); return stream.ToArray(); }
        }
        private void Guard(Action action) { try { action(); } catch(Exception ex) { failure=ex.ToString(); } }
        private void Command(uint peer,ulong entity,byte[] bytes)
        {
            if(peer==0||entity!=ClockState.Entity||bytes.Length<6||bytes[0]!=Marker) return;
            Guard(()=>
            {
                using(var reader=new BinaryReader(new MemoryStream(bytes)))
                {
                    reader.ReadByte(); byte kind=reader.ReadByte(); int number=reader.ReadInt32();
                    if(number==index-1 && kind==3 && lastAck!=null) { channel.Reply(peer,ClockState.Entity,lastAck); return; }
                    if(number!=index||number>=Cases.Length) return;
                    if(kind==1 && bytes.Length==6)
                    {
                        if(stage==0) { needs.GetAnxiety=Baseline; hostAfter=Probe(number); stage=1; }
                        channel.Reply(peer,ClockState.Entity,Packet(2,index,Baseline,hostAfter));
                    }
                    else if(kind==3 && bytes.Length==14 && stage==1)
                    {
                        float before=reader.ReadSingle(), after=reader.ReadSingle();
                        Near(needs.GetAnxiety,hostAfter,"Guest relief changed the host");
                        Near(before,Baseline,"Client starting value"); Near(after,Expected[index],Cases[index]+" client");
                        Near(after,hostAfter,Cases[index]+" host/client parity");
                        Write(index,hostAfter,after);
                        lastAck=Packet(4,index,Baseline,hostAfter,Baseline,after);
                        channel.Reply(peer,ClockState.Entity,lastAck); index++; stage=0;
                    }
                }
            });
        }
        private void Reply(ulong entity,byte[] bytes)
        {
            if(entity!=ClockState.Entity||bytes.Length<6||bytes[0]!=Marker) return;
            Guard(()=>
            {
                using(var reader=new BinaryReader(new MemoryStream(bytes)))
                {
                    reader.ReadByte(); byte kind=reader.ReadByte(); int number=reader.ReadInt32();
                    if(number!=index||number>=Cases.Length) return;
                    if(kind==2 && bytes.Length==14 && stage==0)
                    {
                        Near(reader.ReadSingle(),Baseline,"Host starting value"); hostAfter=reader.ReadSingle();
                        Near(hostAfter,Expected[index],Cases[index]+" host");
                        Near(needs.GetAnxiety,Baseline,"Host relief changed the client");
                        guestAfter=Probe(index); stage=1; nextSend=0;
                    }
                    else if(kind==4 && bytes.Length==22 && stage==1)
                    {
                        Near(reader.ReadSingle(),Baseline,"Acknowledged host baseline"); Near(reader.ReadSingle(),hostAfter,"Acknowledged host result");
                        Near(reader.ReadSingle(),Baseline,"Acknowledged client baseline"); Near(reader.ReadSingle(),guestAfter,"Acknowledged client result");
                        Near(needs.GetAnxiety,guestAfter,"Client result changed while waiting for acknowledgement");
                        Write(index,hostAfter,guestAfter); index++; stage=0; nextSend=0;
                        if(index<Cases.Length) { needs.GetAnxiety=Baseline; seeded=index; }
                    }
                }
            });
        }
        private float Probe(int number)
        {
            var saved=Capture(); var savedVignette=Profile().vignette.settings;
            // SavePersonalTickPatch accounts for computer minutes at the host.
            // These synchronous API probes must not publish synthetic clock history.
            var clock=plugin.World!.Clock;
            var counter=AccessTools.Field(typeof(SharedClock),"computerTicks");
            var actorField=AccessTools.Field(typeof(SharedClock),"computerUser");
            var oldCounter=counter.GetValue(clock); var oldActor=actorField.GetValue(clock);
            float result=Baseline;
            try
            {
                needs.GetAnxiety=Baseline; needs.GetMappleSyrup=needs.GetPoutine=needs.GetEnergy=.2f;
                needs.GetTHCLevel=needs.GetDrunkness=0; needs.Get_isSaving=false; needs.Dead=false;
                foreach(string name in new[]{"EnergyDrinkInBlood","HangoverDebuff","DeathDebuff","Cooldown_Vomit"})
                    AccessTools.Field(typeof(BasicNeeds_Manager),name).SetValue(needs,0);
                if(number<=3)
                {
                    bool sleeping=number==1;
                    uint actor=number==2?plugin.Session!.LocalPeerId:number==3?(plugin.Session!.LocalPeerId==0?1u:0u):ItemLease.Unowned;
                    for(int tick=0;tick<30;tick++)
                    {
                        if(plugin.TestOptions.IsHost)
                        { using(new ComputerTimeScope(actor)) { needs.Tick(sleeping,number==2); gameplay.Tick(720+tick); } }
                        else GuestPersonalMinute.Tick(needs,gameplay,sleeping,720+tick,actor,localPeer:plugin.Session!.LocalPeerId);
                    }
                }
                else if(number==4||number==5)
                { int frames=number==4?300:600; for(int frame=0;frame<frames;frame++) needs.Fill_THC(1f/60f/20f); }
                else if(number==6||number==7)
                {
                    if(number==6) needs.Fill_Alchool(10,30,25);
                    else needs.Fill_Alchool(5,8,15,true);
                    // Complete only our own scheduled native effect synchronously before restoring its fields.
                    needs.CancelInvoke("Alchool_FinishedDrinking");
                    AccessTools.Method(typeof(BasicNeeds_Manager),"Alchool_FinishedDrinking").Invoke(needs,null);
                }
                else if(number==8) needs.Pet_Leo();
                else if(number==9||number==10) needs.DerbyCrash(number==9?2:8);
                else if(number==11) needs.Fill_Anxiety(1);
                else needs.Fill_Everything();
                result=needs.GetAnxiety; Near(result,Expected[number],Cases[number]); return result;
            }
            finally
            {
                Restore(saved); Profile().vignette.settings=savedVignette;
                counter.SetValue(clock,oldCounter); actorField.SetValue(clock,oldActor);
                needs.GetAnxiety=result; Refresh();
            }
        }
        private void Refresh()
        {
            var drunk=(DrunkEffect)AccessTools.Field(typeof(BasicNeeds_Manager),"DrunkEffect").GetValue(needs);
            drunk.Drunkness(needs.GetDrunkness,(int)AccessTools.Field(typeof(BasicNeeds_Manager),"HangoverDebuff").GetValue(needs));
            AccessTools.Method(typeof(BasicNeeds_Manager),"UpdateUI").Invoke(needs,null);
        }
        private void Write(int number,float host,float guest)
        {
            string F(float value)=>value.ToString("R",CultureInfo.InvariantCulture);
            File.AppendAllText(table,Cases[number]+"\t"+F(Baseline)+"\t"+F(host)+"\t"+F(Baseline)+"\t"+F(guest)+"\t"+F(Expected[number])+"\tPASS\n");
        }
        internal bool Tick()
        {
            if(failure!=null) throw new InvalidOperationException(failure);
            Require(Time.unscaledTime-start<90,"Timed out waiting for the other process.");
            if(index==Cases.Length) return true;
            if(plugin.TestOptions.IsHost) UniStormSystem.Instance.TimeFlow=UniStormSystem.EnableFeature.Disabled;
            if(plugin.World?.Channel.FullySynchronized!=true||plugin.World.Clock.TestState.Flowing) return false;
            if(plugin.TestOptions.IsHost) return false;
            if(Time.unscaledTime<nextSend) return false;
            if(stage==0)
            {
                if(seeded!=index) { needs.GetAnxiety=Baseline; seeded=index; }
                channel.Request(ClockState.Entity,Packet(1,index));
            }
            else channel.Request(ClockState.Entity,Packet(3,index,Baseline,guestAfter));
            nextSend=Time.unscaledTime+.4f; return false;
        }
        public void Dispose()
        {
            if(disposed) return; disposed=true;
            channel.CommandRequested-=Command; channel.ReplyReceived-=Reply;
            try { Restore(original); Profile().vignette.settings=vignette; Refresh(); }
            finally { if(plugin.TestOptions.IsHost && UniStormSystem.Instance!=null) UniStormSystem.Instance.TimeFlow=flow; }
        }
    }
}
