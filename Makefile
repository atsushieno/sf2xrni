SOURCES=sf2xrni.cs

CSC=csc # or build make CSC=mcs

sf2xrni.exe : $(SOURCES)
	$(CSC) -platform:x86 -r:NAudio.dll -r:NRenoiseTools.dll $(SOURCES) -debug

clean:
	rm sf2xrni.exe

dist:
	rm -f sf2xrni-latest.zip
	zip sf2xrni-latest.zip sf2xrni.exe sf2xrni.cs NAudio.dll NRenoiseTools.dll ICSharpCode.SharpZipLib.dll README Makefile
