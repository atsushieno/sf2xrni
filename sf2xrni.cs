using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NRenoiseTools;
using NAudio.SoundFont;
using NAudio.Wave;

using SInstrument = NAudio.SoundFont.Instrument;
using XInstrument = NRenoiseTools.RenoiseInstrument;
using SSampleHeader = NAudio.SoundFont.SampleHeader;
using XSample = NRenoiseTools.InstrumentSample;

namespace Commons.Music.Sf2Xrni
{
	static class SF2Extension
	{
		public static SInstrument Instrument (this Zone zone)
		{
			var g = SelectByGenerator (zone, GeneratorEnum.Instrument);
			return g != null ? g.Instrument : null;
		}

		public static ushort KeyRange (this Zone zone)
		{
			var g = SelectByGenerator (zone, GeneratorEnum.KeyRange);
			return g != null ? g.UInt16Amount : (ushort) 0;
		}

		public static ushort VelocityRange (this Zone zone)
		{
			var g = SelectByGenerator (zone, GeneratorEnum.VelocityRange);
			return g != null ? g.UInt16Amount : (ushort) 0;
		}

		public static byte OverridingRootKey (this Zone zone)
		{
			var g = SelectByGenerator (zone, GeneratorEnum.OverridingRootKey);
			return g != null ? g.LowByteAmount : (byte) 0;
		}

		public static int SampleModes (this Zone zone)
		{
			var g = SelectByGenerator (zone, GeneratorEnum.SampleModes);
			return g != null ? g.UInt16Amount : (ushort) 0;
		}

		public static SampleHeader SampleHeader (this Zone zone)
		{
			var g = SelectByGenerator (zone, GeneratorEnum.SampleID);
			return g != null ? g.SampleHeader : null;
		}

		public static Generator SelectByGenerator (this Zone zone, GeneratorEnum type)
		{
			foreach (var g in zone.Generators)
				if (g.GeneratorType == type)
					return g;
			return null;
		}
	}

	public class Driver
	{
		public static void Main (string [] args)
		{
			string filter = null;
			var files = new List<string> ();
			foreach (var arg in args) {
				if (arg.StartsWith ("--filter:"))
					filter = arg.Substring (9);
				else
					files.Add (arg);
			}

			if (filter != null)
				Console.WriteLine ("Applied filer: " + filter);

			foreach (var file in files) {
				var path = Path.ChangeExtension (file, "");
				if (!Directory.Exists (path))
					Directory.CreateDirectory (path);
				var sf2xrni = new Sf2XrniStreamingConverter (path);
				sf2xrni.Import (file, filter);
			}
		}
	}

	public class Sf2XrniStreamingConverter : Sf2Xrni
	{
		string xrni_dir;
		int count;

		public Sf2XrniStreamingConverter (string xrniDir)
		{
			xrni_dir = xrniDir;
		}

		protected override void OnXrniCreated (XInstrument xrni)
		{
			count++;
			string name = xrni.Name;
			var path = Path.Combine (xrni_dir, count + "_" + name) + ".xrni";
			using (var fs = new FileStream (path, FileMode.Create))
				xrni.Save (fs);
		}
	}

	public class Sf2Xrni
	{
		public void Import (string file, string filter)
		{
			var sf2 = new SoundFont (file);
			foreach (var preset in sf2.Presets) {
				if (filter != null && preset.Name.IndexOf (filter) < 0)
					continue;
				Console.WriteLine ("Processing " + preset.Name);
				var xrni = new XInstrument ();
				xrni.Name = NormalizePathName (preset.Name);
				ImportSamples (sf2, preset, xrni);

				OnXrniCreated (xrni);
			}
		}

		void ImportSamples (SoundFont sf2, Preset preset, XInstrument xrni)
		{
			var xl = new List<XSample> ();
			var ml = new List<SampleMap> ();
			var il = new List<int> ();
			foreach (var pzone in preset.Zones) { // perc. bank likely has more than one instrument here.
				var i = pzone.Instrument ();
				var kr = pzone.KeyRange (); // FIXME: where should I use it?
				if (i == null)
					continue; // FIXME: is it possible?

				var vr = pzone.VelocityRange ();
				if (vr != 0 && ((vr & 0xFF00) >> 8) < 127)
					continue; // use one with the highest velocity.

				// an Instrument contains a set of zones that contain sample headers.
//Console.WriteLine ("Instrument: " + i.Name);
				int sampleCount = 0;
				foreach (var izone in i.Zones) {
					var ikr = izone.KeyRange ();
					var ivr = izone.VelocityRange ();
					var sh = izone.SampleHeader ();
					if (sh == null)
						continue; // FIXME: is it possible?
					if (ml.FirstOrDefault (m => m.KeyRange == ikr && m.VelocityRange == ivr && m.SampleHeader == sh) != null)
						continue; // There already is an overlapping one (not sure why such mapping is allowed, but there are such ones)

					if (ivr != 0 && ((ivr & 0xFF00) >> 8) < 127)
						continue; // use one with the highest velocity.

					// FIXME: sample data must become monoral (panpot neutral)
					var xs = ConvertSample (sampleCount++, sh, sf2.SampleData, izone);
					xs.Name = NormalizePathName (sh.SampleName);
					ml.Add (new SampleMap (ikr, ivr, xs, sh));
				}
			}

			ml.Sort ((m1, m2) => m1.KeyLowRange != m2.KeyLowRange ? m1.KeyLowRange - m2.KeyLowRange : m1.KeyHighRange - m2.KeyHighRange);

			int prev = -1;
			foreach (var m in ml) {
				if (m.KeyLowRange == prev)
					continue; // skip ones with equivalent key to the previous one. Likely, right and left, or high volume vs. low volume.
				prev = m.KeyLowRange;
				il.Add (m.KeyLowRange);
				xl.Add (m.Sample);
			}

			xrni.SplitMap = new int [128];
			prev = -1; // follow the previous code.
			int lastValid = -1;
			for (int i = 0; i < ml.Count; i++) {
				var m = ml [i];
				if (m.KeyLowRange == prev)
					continue;
				lastValid = i;
				prev = m.KeyLowRange;

				if (m.KeyHighRange <= 0) { // in case KeyHighRange is invalid...
					for (int k = 0; k < 128; k++)
						xrni.SplitMap [k] = i;
				} else {
					for (int k = m.KeyLowRange; k <= m.KeyHighRange; k++)
						xrni.SplitMap [k] = i;
				}
			}
			if (lastValid >= 0)
				for (int i = ml [ml.Count - 1].KeyHighRange + 1; i < 128; i++)
					xrni.SplitMap [i] = lastValid;

			xrni.Samples = new RenoiseInstrumentSamples ();
			xrni.Samples.Sample = xl.ToArray ();
		}

		class SampleMap
		{
			public SampleMap (ushort keyRange, ushort velocityRange, XSample sample, SampleHeader sh)
			{
				KeyRange = keyRange;
				VelocityRange = velocityRange;
				Sample = sample;
				SampleHeader = sh;
			}

			public ushort KeyRange;
			public ushort VelocityRange;
			public XSample Sample;
			public SSampleHeader SampleHeader;

			public byte KeyLowRange {
				get { return (byte) (KeyRange & 0xFF); }
			}
			public byte KeyHighRange {
				get { return (byte) ((KeyRange & 0xFF00) >> 8); }
			}
			public byte VelocityLowRange {
				get { return (byte) (VelocityRange & 0xFF); }
			}
			public byte VelocityHighRange {
				get { return (byte) ((VelocityRange & 0xFF00) >> 8); }
			}
		}

		List<XInstrument> xinstruments = new List<XInstrument> ();

		public IList<XInstrument> Instruments {
			get { return xinstruments; }
		}

		protected virtual void OnXrniCreated (XInstrument xrni)
		{
			xinstruments.Add (xrni);
		}

		XSample ConvertSample (int count, SSampleHeader sh, byte [] sample, Zone izone)
		{
			// Indices in sf2 are numbers of samples, not byte length. So double them.
			var xs = new XSample ();
			xs.Extension = ".wav";
			xs.LoopStart = 2 * (sh.StartLoop - sh.Start);
			xs.LoopEnd = 2 * (sh.EndLoop - sh.End);
			int sampleModes = izone.SampleModes ();
			xs.LoopMode = sampleModes == 0 ? InstrumentSampleLoopMode.Off : InstrumentSampleLoopMode.Forward;
			xs.Name = String.Format ("Sample{0:D02} ({1})", count, sh.SampleName);
			xs.BaseNote = (sbyte) izone.OverridingRootKey ();
			if (xs.BaseNote == 0)
				xs.BaseNote = (sbyte) sh.OriginalPitch;
//Console.WriteLine ("{0} ({1}/{2}/{3}/{4}) {5}:{6}:{7}:{8}", xs.Name, sh.Start, sh.StartLoop, sh.EndLoop, sh.End, sh.SampleRate != 0xAC44 ? sh.SampleRate.ToString () : "", sh.OriginalPitch != 60 ? sh.OriginalPitch.ToString () : "", sh.PitchCorrection != 0 ? sh.PitchCorrection.ToString () : "", sampleModes);
			xs.FileName = xs.Name + ".wav";
			var ms = new MemoryStream ();
			var wfw = new WaveFileWriter (ms, new WaveFormat ((int) sh.SampleRate, 16, 1));
			wfw.WriteData (sample, 2 * (int) sh.Start, 2 * (int) (sh.End - sh.Start));
			wfw.Close ();
			xs.Buffer = ms.ToArray ();

			return xs;
		}

		string NormalizePathName (string name)
		{
			foreach (char c in Path.GetInvalidPathChars ())
				name = name.Replace (c, '_');
			name = name.Replace (':', '_');
			return name;
		}
	}
}
