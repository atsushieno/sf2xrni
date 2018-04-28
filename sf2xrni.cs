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
		public static int Main (string [] args)
		{
			if (args.Length == 0) {
				Console.WriteLine ("Usage: sf2xrni [sf2files...]");
				return 1;
			}

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
			return 0;
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
			var path = Path.Combine (xrni_dir, NormalizeFileName (count + "_" + name)) + ".xrni";
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

				// an Instrument contains a set of zones that contain sample headers.
				int sampleCount = 0;
				foreach (var izone in i.Zones) {
					var ikr = izone.KeyRange ();
					var ivr = izone.VelocityRange ();
					var sh = izone.SampleHeader ();
					if (sh == null)
						continue; // FIXME: is it possible?

					// FIXME: sample data must become monoral (panpot neutral)
					var xs = ConvertSample (sampleCount++, sh, sf2.SampleData, izone);
					xs.Name = NormalizePathName (sh.SampleName);
					ml.Add (new SampleMap (ikr, ivr, xs, sh));
				}
			}

			ml.Sort ((m1, m2) =>
				m1.KeyLowRange != m2.KeyLowRange ? m1.KeyLowRange - m2.KeyLowRange :
				m1.KeyHighRange != m2.KeyHighRange ? m1.KeyHighRange - m2.KeyHighRange :
				m1.VelocityLowRange != m2.VelocityLowRange ? m1.VelocityLowRange - m2.VelocityLowRange :
				m1.VelocityHighRange - m2.VelocityHighRange);

			int prev = -1;
			foreach (var m in ml) {
				prev = m.KeyLowRange;
				il.Add (m.KeyLowRange);
				xl.Add (m.Sample);
			}

			xrni.SampleSplitMap = new SampleSplitMap ();
			xrni.SampleSplitMap.NoteOnMappings = new SampleSplitMapNoteOnMappings ();
			var nm = new SampleSplitMapping [ml.Count];
			xrni.SampleSplitMap.NoteOnMappings.NoteOnMapping = nm;
			for (int i = 0; i < ml.Count; i++) {
				var m = ml [i];
				var n = new SampleSplitMapping ();
				n.BaseNote = m.Sample.BaseNote;
				n.NoteStart = m.KeyLowRange;
				n.NoteEnd = m.KeyHighRange <= 0 ? 128 : m.KeyHighRange;
				n.SampleIndex = i;
				if (m.VelocityHighRange > 0) {
					n.MapVelocityToVolume = true;
					n.VelocityStart = m.VelocityLowRange;
					n.VelocityEnd = m.VelocityHighRange;
				}
				nm [i] = n;
			}

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
			xs.LoopStart = sh.StartLoop - sh.Start;
			xs.LoopEnd = sh.EndLoop - sh.Start;
			int sampleModes = izone.SampleModes ();
			xs.LoopMode = sampleModes == 0 ? InstrumentSampleLoopMode.Off : InstrumentSampleLoopMode.Forward;
			xs.Name = String.Format ("Sample{0:D02} ({1})", count, sh.SampleName);
			xs.BaseNote = (sbyte) izone.OverridingRootKey ();
//			xs.Volume = (izone.VelocityRange () & 0xFF00 >> 8); // low range
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

		internal static string NormalizePathName (string name)
		{
			return Normalize (name, Path.GetInvalidPathChars ());
		}

		internal static string NormalizeFileName (string name)
		{
			return Normalize (name, Path.GetInvalidFileNameChars ());
		}

		static string Normalize (string name, char [] invalidChars)
		{
			foreach (char c in invalidChars)
				name = name.Replace (c, '_');
			name = name.Replace (':', '_');
			return name;
		}
	}
}
