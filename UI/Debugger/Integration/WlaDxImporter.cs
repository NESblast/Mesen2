﻿using Mesen.Config;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using Mesen.Utilities;
using Mesen.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Mesen.Debugger.Integration;

public class WlaDxImporter : ISymbolProvider
{
	private static Regex _labelRegex = new Regex(@"^([0-9a-fA-F]{2,4}):([0-9a-fA-F]{4}) ([^\s]*)", RegexOptions.Compiled);
	private static Regex _fileRegex = new Regex(@"^([0-9a-fA-F]{4}) ([0-9a-fA-F]{8}) (.*)", RegexOptions.Compiled);
	private static Regex _addrRegex = new Regex(@"^([0-9a-fA-F]{2,4}):([0-9a-fA-F]{4}) ([0-9a-fA-F]{4}):([0-9a-fA-F]{8})", RegexOptions.Compiled);
	private static Regex _fileV2Regex = new Regex(@"^([0-9a-fA-F]{4}):([0-9a-fA-F]{4}) ([0-9a-fA-F]{8}) (.*)", RegexOptions.Compiled);
	private static Regex _addrV2Regex = new Regex(@"^([0-9a-fA-F]{8}) ([0-9a-fA-F]{2}):([0-9a-fA-F]{4}) ([0-9a-fA-F]{4}) ([0-9a-fA-F]{4}):([0-9a-fA-F]{4}):([0-9a-fA-F]{8})", RegexOptions.Compiled);

	private Dictionary<int, SourceFileInfo> _sourceFiles = new Dictionary<int, SourceFileInfo>();
	private Dictionary<string, AddressInfo> _addressByLine = new Dictionary<string, AddressInfo>();
	private Dictionary<string, SourceCodeLocation> _linesByAddress = new Dictionary<string, SourceCodeLocation>();

	public DateTime SymbolFileStamp { get; private set; }
	public string SymbolPath { get; private set; } = "";

	public List<SourceFileInfo> SourceFiles { get { return _sourceFiles.Values.ToList(); } }

	public AddressInfo? GetLineAddress(SourceFileInfo file, int lineIndex)
	{
		AddressInfo address;
		if(_addressByLine.TryGetValue(file.Name.ToString() + "_" + lineIndex.ToString(), out address)) {
			return address;
		}
		return null;
	}

	public AddressInfo? GetLineEndAddress(SourceFileInfo file, int lineIndex)
	{
		return null;
	}

	public string GetSourceCodeLine(int prgRomAddress)
	{
		throw new NotImplementedException();
	}

	public SourceCodeLocation? GetSourceCodeLineInfo(AddressInfo address)
	{
		string key = address.Type.ToString() + address.Address.ToString();
		SourceCodeLocation location;
		if(_linesByAddress.TryGetValue(key, out location)) {
			return location;
		}
		return null;
	}

	public SourceSymbol? GetSymbol(string word, int scopeStart, int scopeEnd)
	{
		return null;
	}

	public AddressInfo? GetSymbolAddressInfo(SourceSymbol symbol)
	{
		return null;
	}

	public SourceCodeLocation? GetSymbolDefinition(SourceSymbol symbol)
	{
		return null;
	}

	public List<SourceSymbol> GetSymbols()
	{
		return new List<SourceSymbol>();
	}

	public int GetSymbolSize(SourceSymbol srcSymbol)
	{
		return 1;
	}

	public void Import(string path, bool showResult)
	{
		SymbolFileStamp = File.GetLastWriteTime(path);

		string basePath = Path.GetDirectoryName(path) ?? "";
		SymbolPath = basePath;

		string[] lines = File.ReadAllLines(path);

		Dictionary<string, CodeLabel> labels = new Dictionary<string, CodeLabel>();

		bool isGameboy = EmuApi.GetRomInfo().CpuTypes.Contains(CpuType.Gameboy);
		int errorCount = 0;

		for(int i = 0; i < lines.Length; i++) {
			string str = lines[i].Trim();
			if(str == "[labels]") {
				for(; i < lines.Length; i++) {
					if(lines[i].Length > 0) {
						Match m = _labelRegex.Match(lines[i]);
						if(m.Success) {
							int bank = Int32.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
							string label = m.Groups[3].Value;
							label = label.Replace('.', '_').Replace(':', '_').Replace('$', '_');

							if(!LabelManager.LabelRegex.IsMatch(label)) {
								//ignore labels that don't respect the label naming restrictions
								errorCount++;
								continue;
							}

							AddressInfo absAddr;
							if(isGameboy) {
								int addr = Int32.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber);
								if(addr >= 0x8000) {
									AddressInfo relAddr = new AddressInfo() { Address = addr, Type = MemoryType.GameboyMemory };
									absAddr = DebugApi.GetAbsoluteAddress(relAddr);
								} else {
									absAddr = new AddressInfo() { Address = bank * 0x4000 + (addr & 0x3FFF), Type = MemoryType.GbPrgRom };
								}
							} else {
								int addr = (bank << 16) | Int32.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber);
								AddressInfo relAddr = new AddressInfo() { Address = addr, Type = MemoryType.SnesMemory };
								absAddr = DebugApi.GetAbsoluteAddress(relAddr);
							}

							if(absAddr.Address < 0) {
								errorCount++;
								continue;
							}

							string orgLabel = label;
							int j = 1;
							while(labels.ContainsKey(label)) {
								label = orgLabel + j.ToString();
								j++;
							}

							if(ConfigManager.Config.Debug.Integration.IsMemoryTypeImportEnabled(absAddr.Type)) {
								labels[label] = new CodeLabel() {
									Label = label,
									Address = (UInt32)absAddr.Address,
									MemoryType = absAddr.Type,
									Comment = "",
									Flags = CodeLabelFlags.None,
									Length = 1
								};
							}
						}
					} else {
						break;
					}
				}
			} else if(str == "[source files]" || str == "[source files v2]") {
				int file_idx = 1;
				int path_idx = 3;
				ref Regex regex = ref _fileRegex;

				// Conversion of indices for supporting WLA-DX V2
				if (str == "[source files v2]") {
					file_idx = 2;
					path_idx = 4;
					regex = ref _fileV2Regex;
				}

				for(; i < lines.Length; i++) {
					if(lines[i].Length > 0) {
						Match m = regex.Match(lines[i]);
						if(m.Success) {
							int fileId = Int32.Parse(m.Groups[file_idx].Value, System.Globalization.NumberStyles.HexNumber);
							//int fileCrc = Int32.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber);
							string filePath = m.Groups[path_idx].Value;

							string fullPath = Path.Combine(basePath, filePath);
							_sourceFiles[fileId] = new SourceFileInfo(filePath, true, new WlaDxFile() { Data = File.Exists(fullPath) ? File.ReadAllLines(fullPath) : new string[0] });
						}
					} else {
						break;
					}
				}
			} else if(str == "[addr-to-line mapping]" || str == "[addr-to-line mapping v2]") {
				int bank_idx = 1;
				int addr_idx = 2;
				int field_idx = 3;
				int line_idx = 4;
				ref Regex regex = ref _addrRegex;

				// Conversion of indices for supporting WLA-DX V2
				if (str == "[addr-to-line mapping v2]") {
					bank_idx = 2;
					addr_idx = 3;
					field_idx = 6;
					line_idx = 7;
					regex = ref _addrV2Regex;
				}

				for(; i < lines.Length; i++) {
					if(lines[i].Length > 0) {
						Match m = regex.Match(lines[i]);
						if(m.Success) {
							int bank = Int32.Parse(m.Groups[bank_idx].Value, System.Globalization.NumberStyles.HexNumber);
							int addr = (bank << 16) | Int32.Parse(m.Groups[addr_idx].Value, System.Globalization.NumberStyles.HexNumber);
							
							int fileId = Int32.Parse(m.Groups[field_idx].Value, System.Globalization.NumberStyles.HexNumber);
							int lineNumber = Int32.Parse(m.Groups[line_idx].Value, System.Globalization.NumberStyles.HexNumber);

							if(lineNumber <= 1) {
								//Ignore line number 0 and 1, seems like bad data?
								errorCount++;
								continue;
							}

							AddressInfo absAddr = new AddressInfo() { Address = addr, Type = MemoryType.SnesPrgRom };
							_addressByLine[_sourceFiles[fileId].Name + "_" + lineNumber.ToString()] = absAddr;
							_linesByAddress[absAddr.Type.ToString() + absAddr.Address.ToString()] = new SourceCodeLocation(_sourceFiles[fileId], lineNumber);
						}
					} else {
						break;
					}
				}
			}
		}

		LabelManager.SetLabels(labels.Values, true);

		if(showResult) {
			if(errorCount > 0) {
				MesenMsgBox.Show(null, "ImportLabelsWithErrors", MessageBoxButtons.OK, MessageBoxIcon.Warning, labels.Count.ToString(), errorCount.ToString());
			} else {
				MesenMsgBox.Show(null, "ImportLabels", MessageBoxButtons.OK, MessageBoxIcon.Info, labels.Count.ToString());
			}
		}
	}

	class WlaDxFile : IFileDataProvider
	{
		public string[] Data { get; init; } = Array.Empty<string>();
	}
}
