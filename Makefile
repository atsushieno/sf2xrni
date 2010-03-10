SOURCES=sf2xrni.cs

sf2xrni.exe : $(SOURCES)
	gmcs -r:NAudio.dll -r:NRenoiseTools.dll $(SOURCES) -debug

clean:
	rm sf2xrni.exe

dist:
	rm -f sf2xrni-latest.zip
	zip sf2xrni-latest.zip sf2xrni.exe sf2xrni.cs NAudio.dll NRenoiseTools.dll NRenoiseTools.XmlSerializers.dll ICSharpCode.SharpZipLib.dll README Makefile
