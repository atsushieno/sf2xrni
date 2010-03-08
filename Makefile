SOURCES=sf2xrni.cs

sf2xrni.exe : $(SOURCES)
	gmcs -r:NAudio.dll -r:NRenoiseTools.dll $(SOURCES) -debug

clean:
	rm sf2xrni.exe
