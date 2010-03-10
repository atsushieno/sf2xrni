using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NRenoiseTools;
using NAudio.SoundFont;
using NAudio.Wave;

using SInstrument = NAudio.SoundFont.Instrument;
using XInstrument = NRenoiseTools.Instrument;
using SSampleHeader = NAudio.SoundFont.SampleHeader;
using XSample = NRenoiseTools.Sample;

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
			foreach (var file in args) {
				var path = Path.ChangeExtension (file, "");
				if (!Directory.Exists (path))
					Directory.CreateDirectory (path);
				var sf2xrni = new Sf2XrniStreamingConverter (path);
				sf2xrni.Import (file);
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
			var path = Path.Combine (xrni_dir, count + "_" + xrni.Name) + ".xrni";
			using (var fs = new FileStream (path, FileMode.Create))
				xrni.Save (fs);
		}
	}

	public class Sf2Xrni
	{
		public void Import (string file)
		{
			var sf2 = new SoundFont (file);
			foreach (var preset in sf2.Presets) {
				Console.WriteLine ("Processing " + preset.Name);
				var xrni = new XInstrument ();
				xrni.Name = preset.Name;
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
				var r = pzone.KeyRange (); // FIXME: where should I use it?
				if (i == null)
					continue; // FIXME: is it possible?
				// an Instrument contains a set of zones that contain
				int sampleCount = 0;
				foreach (var izone in i.Zones) {
					var ir = izone.KeyRange ();
					var sh = izone.SampleHeader ();
					if (sh == null)
						continue; // FIXME: is it possible?
					// FIXME: sample data must become monoral (panpot neutral)
					var xs = ConvertSample (sampleCount++, sh, sf2.SampleData, izone);
					xs.Name = sh.SampleName;
					ml.Add (new SampleMap (ir, xs));
				}
			}

			ml.Sort ((m1, m2) => m1.LowRange - m2.LowRange);

			int prev = -1;
			foreach (var m in ml) {
				if (m.LowRange == prev)
					continue; // skip ones with equivalent key to the previous one. Likely, right and left, or high volume vs. low volume.
				prev = m.LowRange;
				il.Add (m.LowRange);
				xl.Add (m.XSample);
			}

			xrni.SplitMap = new int [128];
			int idx = -1;
			for (int i = 0; i < 128; i++) {
				// It is somewhat buggy, includes wrong values for low key area. But replacing it with "while" brings another bug. And using HighRange <= i also gives wrong value too.
				if (idx + 1 < ml.Count && ml [idx + 1].LowRange >= i)
					idx++;
				xrni.SplitMap [i] = idx;
			}
			xrni.Samples = xl.ToArray ();
		}

		struct SampleMap
		{
			public SampleMap (ushort range, XSample sample)
			{
				Range = range;
				XSample = sample;
			}

			public ushort Range;
			public XSample XSample;

			public byte LowRange {
				get { return (byte) (Range & 0xFF); }
			}
			public byte HighRange {
				get { return (byte) ((Range & 0xFF00) >> 8); }
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
			xs.LoopStart = 2 * (sh.StartLoop - sh.Start);
			xs.LoopEnd = 2 * (sh.EndLoop - sh.End);
			int sampleModes = izone.SampleModes ();
			xs.LoopMode = sampleModes == 0 ? SampleLoopMode.Off : SampleLoopMode.Forward;
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
	}
}
