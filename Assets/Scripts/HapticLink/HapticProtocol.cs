using System.IO;

/// <summary>
/// The wire format between the PC bridge (HapticBridge, reads the Touch with OpenHaptics) and the headset (HapticLinkClient).
/// One TCP stream, little endian, frames of a type byte plus a fixed payload. This file is compiled by both projects
/// (Assets/Scripts/HapticLink and HapticBridge/HapticBridge.csproj), so it uses nothing from Unity.
/// Everything is in the device's own frame as OpenHaptics reports it: millimetres and newtons, right handed, +X right, +Y up,
/// +Z toward the user. The headset converts to Unity's.
/// </summary>
public static class HapticProtocol
{
    public const int Port = 5555;
    public const byte StateType = 1, CommandType = 2, RunType = 3, SavedType = 4;
    public const int MaxRunBytes = 32 << 20;         // the bridge refuses a run frame bigger than this: a garbled length must not allocate gigabytes
    public const int StateSize = 1 + 4 + 4 + 4 + 4 + 12 + 64, CommandSize = 1 + 4 + 4 + 12;

    public const int Button1 = 1, Button2 = 2;       // StateFrame.buttons: the stylus' two buttons (HD_CURRENT_BUTTONS)
    public const int Inkwell = 1, ForceOn = 2;       // StateFrame.flags: stylus in the inkwell; the bridge is letting force out
    public const int Enable = 1;                     // CommandFrame.flags: the headset wants the force applied

    /// <summary>Bridge to headset: where the stylus is and what the bridge is doing. Sent about every 8 ms.</summary>
    public sealed class StateFrame
    {
        public uint seq;             // counts the frames the bridge has sent
        public uint ack;             // the last command seq the bridge received: the headset times the round trip with it
        public int buttons, flags;
        public float fx, fy, fz;     // the force being applied right now, N (after the bridge's limit and smoothing)
        public readonly float[] m = new float[16]; // HD_CURRENT_TRANSFORM: column major, the tip (mm) in 12..14

        public void Write(BinaryWriter w)
        {
            w.Write(StateType); w.Write(seq); w.Write(ack); w.Write(buttons); w.Write(flags);
            w.Write(fx); w.Write(fy); w.Write(fz);
            foreach (var v in m) w.Write(v);
        }

        /// <summary>Reads the payload; the type byte has been read already.</summary>
        public void Read(BinaryReader r)
        {
            seq = r.ReadUInt32(); ack = r.ReadUInt32(); buttons = r.ReadInt32(); flags = r.ReadInt32();
            fx = r.ReadSingle(); fy = r.ReadSingle(); fz = r.ReadSingle();
            for (int i = 0; i < 16; i++) m[i] = r.ReadSingle();
        }

        public void CopyFrom(StateFrame o)
        {
            seq = o.seq; ack = o.ack; buttons = o.buttons; flags = o.flags; fx = o.fx; fy = o.fy; fz = o.fz;
            System.Array.Copy(o.m, m, 16);
        }
    }

    /// <summary>Headset to bridge: the force it wants on the stylus. The bridge drops it to zero if these stop coming.</summary>
    public sealed class CommandFrame
    {
        public uint seq;
        public int flags;
        public float fx, fy, fz;     // N

        public void Write(BinaryWriter w)
        {
            w.Write(CommandType); w.Write(seq); w.Write(flags); w.Write(fx); w.Write(fy); w.Write(fz);
        }

        public void Read(BinaryReader r)
        {
            seq = r.ReadUInt32(); flags = r.ReadInt32(); fx = r.ReadSingle(); fy = r.ReadSingle(); fz = r.ReadSingle();
        }
    }

    /// <summary>
    /// Headset to bridge: a finished run of the force graph as CSV text (variable length: type, id, byte count, UTF-8), for the bridge to save
    /// on the PC. `id` names the run, so one sent again because the link dropped before the answer came is saved once.
    /// </summary>
    public sealed class RunFrame
    {
        public long id;
        public string csv = "";

        public void Write(BinaryWriter w)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
            w.Write(RunType); w.Write(id); w.Write(bytes.Length); w.Write(bytes);
        }

        public void Read(BinaryReader r)
        {
            id = r.ReadInt64();
            int n = r.ReadInt32();
            if (n < 0 || n > MaxRunBytes) throw new InvalidDataException($"a run of {n} bytes");
            csv = System.Text.Encoding.UTF8.GetString(r.ReadBytes(n));
        }
    }

    /// <summary>Bridge to headset: the run `id` is saved on the PC, as the file `name`.</summary>
    public sealed class SavedFrame
    {
        public long id;
        public string name = "";

        public void Write(BinaryWriter w) { w.Write(SavedType); w.Write(id); w.Write(name); }

        public void Read(BinaryReader r) { id = r.ReadInt64(); name = r.ReadString(); }
    }
}
