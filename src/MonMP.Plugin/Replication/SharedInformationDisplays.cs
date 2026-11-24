using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using TMPro;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedInformationDisplays : IDisposable
    {
        private static readonly FieldInfo SuperSign = AccessTools.Field(typeof(Store_KaliGas), "PriceSuperFuel");
        private static readonly FieldInfo PumpLitres = AccessTools.Field(typeof(Kaligaz_FuelGun), "FuelAdded");
        private static readonly MethodInfo PumpText = AccessTools.Method(typeof(Kaligaz_FuelGun), "ChangePriceText");
        private readonly bool host;
        private readonly Store_PoliceInterceptor? police;
        private readonly Store_KaliGas[] stations;
        private readonly Kaligaz_FuelGun[] pumps;
        private readonly Dictionary<TextMeshPro, string> originalSigns = new Dictionary<TextMeshPro, string>();
        private readonly WorldInformationState original;
        private WorldInformationState current;
        private byte[] last;
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextCapture;
        private bool disposed, received;
        private LetterDisplay? letterDisplay;

        private sealed class LetterDisplay
        {
            internal readonly Envelope_Police_Ticket Letter;
            internal readonly TextMeshPro Lines, Bill;
            internal readonly string OriginalLines, OriginalBill;
            internal LetterDisplay(Envelope_Police_Ticket letter, TextMeshPro lines, TextMeshPro bill)
            { Letter = letter; Lines = lines; Bill = bill; OriginalLines = lines.text; OriginalBill = bill.text; }
        }

        internal SharedInformationDisplays(Gameplay gameplay, bool host)
        {
            this.host = host; police = gameplay.Store_PoliceInterceptor;
            stations = UnityEngine.Object.FindObjectsOfType<Store_KaliGas>(true).Where(s => s.gameObject.scene.IsValid()).ToArray();
            pumps = UnityEngine.Object.FindObjectsOfType<Kaligaz_FuelGun>(true).Where(p => p.gameObject.scene.IsValid()).ToArray();
            foreach (var station in stations)
                if (SuperSign.GetValue(station) is TextMeshPro sign && sign != null) originalSigns[sign] = sign.text;
            original = current = Capture(); last = original.Encode();
        }

        private WorldInformationState Capture() => new WorldInformationState(Store_KaliGas.FuelPrice_Super, Store_KaliGas.FuelPrice_Ordinaire,
            Store_KaliGas.FuelPrice_Super_Dock, Store_KaliGas.FuelPrice_Ordinaire_Dock, police != null, police?.Get_BillToPay ?? 0f,
            police == null ? Array.Empty<PoliceTicketDisplay>() : police.Get_ActiveTickets.Take(WorldInformationState.MaximumTickets)
                .Select(t => new PoliceTicketDisplay((byte)t.TicketType, t.PlayerSpeed, t.SpeedLimit, t.GetTicketPrice)).ToArray());

        internal IEnumerable<WorldRecord> InitialRecords()
        { yield return new WorldRecord(WorldInformationState.Entity, 1, WorldInformationState.Adapter, original.Encode()); }

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            world.SnapshotApplied += Snapshot; world.Changed += Changed;
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        { if (!host && records.TryGetValue(WorldInformationState.Entity, out var record)) Apply(record); }
        private void Changed(WorldRecord record)
        { if (!host && record.Id == WorldInformationState.Entity) Apply(record); }
        private void Apply(WorldRecord record)
        {
            if (disposed) return;
            if (record.Adapter != WorldInformationState.Adapter) throw new InvalidDataException("Invalid world information adapter.");
            var state = WorldInformationState.Decode(record.Payload);
            ApplyPrices(state, !received || !PricesMatch(state));
            current = state; received = true;
            RefreshPoliceLetter();
        }

        private static bool PricesMatch(WorldInformationState state) => Store_KaliGas.FuelPrice_Super == state.SuperPrice &&
            Store_KaliGas.FuelPrice_Ordinaire == state.OrdinaryPrice && Store_KaliGas.FuelPrice_Super_Dock == state.DockSuperPrice &&
            Store_KaliGas.FuelPrice_Ordinaire_Dock == state.DockOrdinaryPrice;
        private void ApplyPrices(WorldInformationState state, bool refresh)
        {
            Store_KaliGas.FuelPrice_Super = state.SuperPrice; Store_KaliGas.FuelPrice_Ordinaire = state.OrdinaryPrice;
            Store_KaliGas.FuelPrice_Super_Dock = state.DockSuperPrice; Store_KaliGas.FuelPrice_Ordinaire_Dock = state.DockOrdinaryPrice;
            if (!refresh) return;
            string text = (state.SuperPrice * 1000f).ToString("000");
            text = text[0].ToString() + text[1] + " " + text[2];
            foreach (var station in stations)
                if (station != null && SuperSign.GetValue(station) is TextMeshPro sign && sign != null) sign.text = text;
            foreach (var pump in pumps)
                if (pump != null) PumpText.Invoke(pump, new[] { PumpLitres.GetValue(pump) });
        }

        internal void Update()
        {
            if (disposed || session?.IsRunning != true || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 1f;
            if (host)
            {
                var state = Capture(); var payload = state.Encode();
                if (!payload.SequenceEqual(last)) { last = payload; current = state; channel!.Publish(WorldInformationState.Entity, payload); }
            }
            else
            {
                if (received && !PricesMatch(current)) ApplyPrices(current, true);
                if (letterDisplay != null && (letterDisplay.Letter == null || !letterDisplay.Letter.isEquiped)) RestorePoliceLetter();
            }
        }

        internal void ShowPoliceLetter(Envelope_Police_Ticket letter, TextMeshPro lines, TextMeshPro bill)
        {
            if (host || disposed || session?.IsRunning != true || letter == null || !letter.isEquiped || lines == null || bill == null) return;
            if (letterDisplay != null && letterDisplay.Letter != letter) RestorePoliceLetter();
            // Equiped has just rendered the private native baseline; retain it for unequip/teardown.
            letterDisplay = new LetterDisplay(letter, lines, bill);
            RefreshPoliceLetter();
        }

        internal void HidePoliceLetter(Envelope_Police_Ticket letter)
        { if (letterDisplay?.Letter == letter) RestorePoliceLetter(); }

        private void RefreshPoliceLetter()
        {
            var display = letterDisplay;
            if (display == null || display.Letter == null || !display.Letter.isEquiped) return;
            if (!received || !current.PoliceAvailable)
            {
                if (display.Lines != null) display.Lines.text = !received ? "The host's ticket information is still loading." : "The host's police ticket information is unavailable.";
                if (display.Bill != null) display.Bill.text = "";
                return;
            }
            if (display.Lines != null) display.Lines.text = TicketLines(current, false);
            if (display.Bill != null) display.Bill.text = "<mark=#000000aa>" + current.PoliceTotal + "$</mark>";
        }

        private void RestorePoliceLetter()
        {
            var display = letterDisplay; letterDisplay = null;
            if (display == null) return;
            if (display.Lines != null) display.Lines.text = display.OriginalLines;
            if (display.Bill != null) display.Bill.text = display.OriginalBill;
        }

        private static string TicketLines(WorldInformationState state, bool white)
        {
            var text = new StringBuilder(white ? "<mark=#00000088><color=white>" : "<mark=#00000088>");
            foreach (var ticket in state.Tickets)
            {
                var type = (PoliceTicketData.Ticket_Type)ticket.Type;
                text.Append("<nobr><b>").Append(TEXT.Get_PoliceTicketName(type)).Append("</b>\t");
                if (type == PoliceTicketData.Ticket_Type.Speeding)
                    text.Append(Mathf.Ceil(ticket.PlayerSpeed)).Append("km/h ").Append(TEXT.Get_PoliceTicketReason(type)).Append(' ').Append(Mathf.Ceil(ticket.SpeedLimit)).Append("km/h\t");
                else text.Append(TEXT.Get_PoliceTicketReason(type)).Append('\t');
                text.Append("<u><b>").Append(ticket.Price).Append("$</b></u></nobr>\n");
            }
            text.Append(white ? "</color></mark>" : "</mark>");
            return text.ToString();
        }

        internal bool TryShowPolicePhone(out int result)
        {
            result = 1;
            if (host || disposed || session?.IsRunning != true) return false;
            if (!received || !current.PoliceAvailable)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable(!received ? "The host's ticket information is still loading." : "The host's police ticket information is unavailable.");
                return true;
            }
            var message = Singleton<UI_EventMessage>.i;
            if (current.PoliceTotal <= 0)
            {
                message.Show(TEXT.GetText("PHONE_Police_NoTicketsToPay"), TEXT.GetText("Police_Ticket_SenderName"), 10f, UI_EventMessage.EventIconType.Phone);
                return true;
            }
            var text = new StringBuilder(TicketLines(current, true));
            text.Append("\n\n").Append(TEXT.GetText("PHONE_Police_TotalToPay")).Append(" <mark=#000000aa>")
                .Append(current.PoliceTotal).Append("$</mark>\n\n <b>").Append(TEXT.GetText("PHONE_GRID_HydroCut_PressOneToPay")).Append(" </b>");
            message.Show(text.ToString(), TEXT.GetText("Police_Ticket_SenderName"), 18f, UI_EventMessage.EventIconType.Phone);
            result = 5;
            return true;
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            RestorePoliceLetter();
            if (host || !received) return;
            ApplyPrices(original, true);
            foreach (var sign in originalSigns) if (sign.Key != null) sign.Key.text = sign.Value;
        }
    }
}
