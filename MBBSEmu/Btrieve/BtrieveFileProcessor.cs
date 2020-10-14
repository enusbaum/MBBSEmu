using MBBSEmu.Btrieve.Enums;
using MBBSEmu.Extensions;
using MBBSEmu.IO;
using MBBSEmu.Logging;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;

namespace MBBSEmu.Btrieve
{
    /// <summary>
    ///     The BtrieveFileProcessor class is used to handle Loading, Parsing, and Querying legacy Btrieve Files
    ///
    ///     Legacy Btrieve files (.DAT) are converted on load to MBBSEmu format files (.EMU), which are JSON representations
    ///     of the underlying Btrieve Data. This means the legacy .DAT files are only used once on initial load and are not
    ///     modified. All Inserts & Updates happen within the new .EMU JSON files.
    /// </summary>
    public class BtrieveFileProcessor
    {
        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger(typeof(CustomLogger));

        private readonly IFileUtility _fileFinder;

        /// <summary>
        ///     File Name of the Btrieve File currently loaded into the Processor
        /// </summary>
        public string LoadedFileName { get; set; }

        /// <summary>
        ///     File Path of the Btrieve File currently loaded into the Processor
        /// </summary>
        public string LoadedFilePath { get; set; }

        /// <summary>
        ///     Total Size of the Loaded Btrieve File
        /// </summary>
        public int LoadedFileSize { get; set; }

        /// <summary>
        ///     Btrieve File Loaded into the Processor
        /// </summary>
        public BtrieveFile LoadedFile { get; set; }

        /// <summary>
        ///     Current Position (offset) of the Processor in the Btrieve File
        /// </summary>
        public uint Position { get; set; }

        /// <summary>
        ///     The Previous Query that was executed
        /// </summary>
        private BtrieveQuery PreviousQuery { get; set; }

        /// <summary>
        ///     Constructor to load the specified Btrieve File at the given Path
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="path"></param>
        public BtrieveFileProcessor(IFileUtility fileUtility, string fileName, string path)
        {
            _fileFinder = fileUtility;

            if (string.IsNullOrEmpty(path))
                path = Directory.GetCurrentDirectory();

            if (!Path.EndsInDirectorySeparator(path))
                path += Path.DirectorySeparatorChar;

            LoadedFilePath = path;
            LoadedFileName = _fileFinder.FindFile(path, fileName);

            //If a .EMU version exists, load it over the .DAT file
            var jsonFileName = LoadedFileName.ToUpper().Replace(".DAT", ".EMU");
            if (File.Exists(Path.Combine(LoadedFilePath, jsonFileName)))
            {
                LoadedFileName = jsonFileName;
                LoadJson(LoadedFilePath, LoadedFileName);
            }
            else
            {
                LoadBtrieve(LoadedFilePath, LoadedFileName);
                SaveJson();
            }

            //Ensure loaded records (regardless of source) are in order by their offset within the Btrieve file
            LoadedFile.Records = LoadedFile.Records.OrderBy(x => x.Offset).ToList();

            //Set Position to First Record
            Position = LoadedFile.Records.OrderBy(x => x.Offset).FirstOrDefault()?.Offset ?? 0;
        }

        /// <summary>
        ///     Loads a Btrieve .DAT File
        /// </summary>
        /// <param name="path"></param>
        /// <param name="fileName"></param>
        private void LoadBtrieve(string path, string fileName)
        {
            //Sanity Check if we're missing .DAT files and there are available .VIR files that can be used
            var virginFileName = fileName.ToUpper().Replace(".DAT", ".VIR");
            if (!File.Exists(Path.Combine(path, fileName)) && File.Exists(Path.Combine(path, virginFileName)))
            {
                File.Copy(Path.Combine(path, virginFileName), Path.Combine(path, fileName));
                _logger.Warn($"Created {fileName} by copying {virginFileName} for first use");
            }

            //If we're missing a DAT file, just bail. Because we don't know the file definition, we can't just create a "blank" one.
            if (!File.Exists(Path.Combine(path, fileName)))
            {
                _logger.Error($"Unable to locate existing btrieve file {fileName}");
                throw new FileNotFoundException($"Unable to locate existing btrieve file {fileName}");
            }

            var fileData = File.ReadAllBytes(Path.Combine(path, fileName));
            LoadedFile = new BtrieveFile(fileData) { FileName = LoadedFileName };
            LoadedFileSize = fileData.Length;
#if DEBUG
            _logger.Info($"Opened {fileName} and read {LoadedFile.Data.Length} bytes");
#endif
            //Only Parse Keys if they are defined
            if (LoadedFile.KeyCount > 0)
                LoadBtrieveKeyDefinitions();

            //Only load records if there are any present
            if (LoadedFile.RecordCount > 0)
                LoadBtrieveRecords();

            CreateSqliteDB(Path.Combine(path, Path.ChangeExtension(fileName, "DB")));
        }

        /// <summary>
        ///     Loads an MBBSEmu representation of a Btrieve File
        /// </summary>
        /// <param name="path"></param>
        /// <param name="fileName"></param>
        private void LoadJson(string path, string fileName)
        {
            LoadedFile = JsonConvert.DeserializeObject<BtrieveFile>(File.ReadAllText(Path.Combine(path, fileName)));
        }

        /// <summary>
        ///     Saves the MBBSEmu representation of a Btrieve File to disk
        /// </summary>
        private void SaveJson()
        {
            var jsonFileName = LoadedFileName.ToUpper().Replace(".DAT", ".EMU");
            File.WriteAllText(Path.Combine(LoadedFilePath, jsonFileName),
                JsonConvert.SerializeObject(LoadedFile, Formatting.Indented));
        }

        /// <summary>
        ///     Loads Btrieve Key Definitions from the Btrieve DAT File Header
        /// </summary>
        private void LoadBtrieveKeyDefinitions()
        {
            ushort keyDefinitionBase = 0x110;
            const ushort keyDefinitionLength = 0x1E;
            ReadOnlySpan<byte> btrieveFileContentSpan = LoadedFile.Data;

            //Check for Log Key
            if (btrieveFileContentSpan[0x10C] == 1)
            {
                _logger.Warn($"Btireve Log Key Present in {LoadedFile.FileName}");
                LoadedFile.LogKeyPresent = true;
            }

            ushort totalKeys = LoadedFile.KeyCount;
            ushort currentKeyNumber = 0;
            while (currentKeyNumber < totalKeys)
            {
                var keyDefinition = new BtrieveKeyDefinition { Data = btrieveFileContentSpan.Slice(keyDefinitionBase, keyDefinitionLength).ToArray() };

                keyDefinition.Number = currentKeyNumber;

                //If it's a segmented key, don't increment so the next key gets added to the same ordinal as an additional segment
                if (!keyDefinition.Attributes.HasFlag(EnumKeyAttributeMask.SegmentedKey))
                    currentKeyNumber++;

#if DEBUG
                _logger.Info("----------------");
                _logger.Info("Loaded Key Definition:");
                _logger.Info("----------------");
                _logger.Info($"Number: {keyDefinition.Number}");
                _logger.Info($"Total Records: {keyDefinition.TotalRecords}");
                _logger.Info($"Data Type: {keyDefinition.DataType}");
                _logger.Info($"Attributes: {keyDefinition.Attributes}");
                _logger.Info($"Position: {keyDefinition.Position}");
                _logger.Info($"Length: {keyDefinition.Length}");
                _logger.Info("----------------");
#endif
                if (!LoadedFile.Keys.TryGetValue(keyDefinition.Number, out var key))
                {
                    key = new BtrieveKey(keyDefinition);
                    LoadedFile.Keys.Add(keyDefinition.Number, key);
                }
                else
                {
                    key.Segments.Add(keyDefinition);
                }


                keyDefinitionBase += keyDefinitionLength;
            }
        }

        /// <summary>
        ///     Loads Btrieve Records from Data Pages
        /// </summary>
        private void LoadBtrieveRecords()
        {
            var recordsLoaded = 0;

            //Starting at 1, since the first page is the header
            for (var i = 1; i <= LoadedFile.PageCount; i++)
            {
                var pageOffset = (LoadedFile.PageLength * i);
                var recordsInPage = (LoadedFile.PageLength / LoadedFile.PhysicalRecordLength);

                //Key Page
                if (BitConverter.ToUInt32(LoadedFile.Data, pageOffset + 0x8) == uint.MaxValue)
                    continue;

                //Key Constraint Page
                if (LoadedFile.Data[pageOffset + 0x6] == 0xAC)
                    continue;

                //Verify Data Page
                if (!LoadedFile.Data[pageOffset + 0x5].IsNegative())
                {
                    _logger.Warn(
                        $"Skipping Non-Data Page, might have invalid data - Page Start: 0x{pageOffset + 0x5:X4}");
                    continue;
                }

                //Page data starts 6 bytes in
                pageOffset += 6;
                for (var j = 0; j < recordsInPage; j++)
                {
                    if (recordsLoaded == LoadedFile.RecordCount)
                        break;

                    var recordArray = new byte[LoadedFile.RecordLength];
                    Array.Copy(LoadedFile.Data, pageOffset + (LoadedFile.PhysicalRecordLength * j), recordArray, 0, LoadedFile.RecordLength);

                    //End of Page 0xFFFFFFFF
                    if (BitConverter.ToUInt32(recordArray, 0) == uint.MaxValue)
                        continue;

                    LoadedFile.Records.Add(new BtrieveRecord((uint)(pageOffset + (LoadedFile.PhysicalRecordLength * j)), recordArray));
                    recordsLoaded++;
                }
            }
#if DEBUG
            _logger.Info($"Loaded {recordsLoaded} records from {LoadedFile.FileName}. Resetting cursor to 0");
#endif
        }

        /// <summary>
        ///     Sets Position to the offset of the first Record in the loaded Btrieve File
        /// </summary>
        /// <returns></returns>
        public ushort StepFirst()
        {
            Position = LoadedFile.Records.OrderBy(x => x.Offset).FirstOrDefault()?.Offset ?? 0;
            return Position > 0 ? (ushort) 1 : (ushort) 0;
        }

        /// <summary>
        ///     Sets Position to the offset of the next logical Record in the loaded Btrieve File
        /// </summary>
        /// <returns></returns>
        public ushort StepNext()
        {
            var nextRecord = LoadedFile.Records.OrderBy(x => x.Offset).FirstOrDefault(x => x.Offset > Position);

            if (nextRecord == null)
                return 0;

            Position = nextRecord.Offset;
            return 1;
        }

        /// <summary>
        ///     Sets Position to the offset of the next logical record in the loaded Btrieve File
        /// </summary>
        /// <returns></returns>
        public ushort StepPrevious()
        {
            var previousRecord = LoadedFile.Records.Where(x => x.Offset < Position).OrderByDescending(x => x.Offset)
                .FirstOrDefault();

            if (previousRecord == null)
                return 0;

            Position = previousRecord.Offset;
            return 1;
        }

        /// <summary>
        ///     Sets Position to the offset of the last Record in the loaded Btrieve File
        /// </summary>
        /// <returns></returns>
        public ushort StepLast()
        {
            var lastRecord = LoadedFile.Records.OrderByDescending(x => x.Offset).FirstOrDefault();

            if (lastRecord == null)
                return 0;

            Position = lastRecord.Offset;
            return 1;
        }

        /// <summary>
        ///     Returns the Record at the current Position
        /// </summary>
        /// <returns></returns>
        public byte[] GetRecord() => GetRecord(Position).Data;

        /// <summary>
        ///     Returns the Record at the specified Offset
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public BtrieveRecord GetRecord(uint offset)
        {
            var result = LoadedFile.Records.FirstOrDefault(x => x.Offset == offset);

            if (result == null)
            {
                _logger.Error($"No Record found at offset {offset}");
            }
            else
            {
                Position = offset;
            }

            return result;
        }

        /// <summary>
        ///     Updates the Record at the current Position
        /// </summary>
        /// <param name="recordData"></param>
        public void Update(byte[] recordData) => Update(Position, recordData);

        /// <summary>
        ///     Updates the Record at the specified Offset
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="recordData"></param>
        public void Update(uint offset, byte[] recordData)
        {
            if (recordData.Length != LoadedFile.RecordLength)
                throw new Exception($"Invalid Btrieve Record. Expected Length {LoadedFile.RecordLength}, Actual Length {recordData.Length}");

            //Find the Record to Update
            foreach (var r in LoadedFile.Records)
            {
                if (r.Offset != offset) continue;

                //Update It
                r.Data = recordData;
                break;
            }

            //Save the Change
            SaveJson();
        }

        /// <summary>
        ///     Inserts a new Btrieve Record at the End of the currently loaded Btrieve File
        /// </summary>
        /// <param name="recordData"></param>
        public void Insert(byte[] recordData)
        {
            if (recordData.Length != LoadedFile.RecordLength)
                _logger.Warn($"Btrieve Record Size Mismatch. Expected Length {LoadedFile.RecordLength}, Actual Length {recordData.Length} ({LoadedFile})");

            //Make it +1 of the last record loaded, or make it 1 if it's the first
            var newRecordOffset = LoadedFile.Records.OrderByDescending(x => x.Offset).FirstOrDefault()?.Offset + 1 ?? 1;

            LoadedFile.Records.Add(new BtrieveRecord(newRecordOffset, recordData));

#if DEBUG
            _logger.Info($"Inserted Record into {LoadedFile.FileName} (Offset: {newRecordOffset})");
#endif
            SaveJson();
        }

        /// <summary>
        ///     Deletes the Btrieve Record at the Current Position within the File
        /// </summary>
        /// <returns></returns>
        public bool Delete()
        {
            if (LoadedFile.Records.Count == 0)
                return false;

            var recordsDeleted = LoadedFile.Records.RemoveAll(x => x.Offset == Position);

            if(recordsDeleted > 1)
                _logger.Warn($"{recordsDeleted} Records found at Offset {Position} were deleted");

            return recordsDeleted > 0;
        }

        /// <summary>
        ///     Deletes all records within the current Btrieve File
        /// </summary>
        public void DeleteAll()
        {
            _logger.Warn($"Deleting all Records from {LoadedFileName} ({LoadedFile.RecordCount} records)");
            LoadedFile.Records.Clear();
            SaveJson();
        }

        /// <summary>
        ///     Performs a Step based Seek on the loaded Btrieve File
        /// </summary>
        /// <param name="operationCode"></param>
        /// <returns></returns>
        public ushort Seek(EnumBtrieveOperationCodes operationCode)
        {
            return operationCode switch
            {
                EnumBtrieveOperationCodes.GetFirst => StepFirst(),
                EnumBtrieveOperationCodes.GetNext => StepNext(),
                EnumBtrieveOperationCodes.GetPrevious => StepPrevious(),
                _ => throw new Exception($"Unsupported Btrieve Operation: {operationCode}")
            };
        }

        /// <summary>
        ///     Performs a Key Based Query on the loaded Btrieve File
        /// </summary>
        /// <param name="keyNumber"></param>
        /// <param name="key"></param>
        /// <param name="btrieveOperationCode"></param>
        /// <param name="newQuery"></param>
        /// <returns></returns>
        public ushort SeekByKey(ushort keyNumber, ReadOnlySpan<byte> key, EnumBtrieveOperationCodes btrieveOperationCode, bool newQuery = true)
        {
            BtrieveQuery currentQuery;

            if (newQuery)
            {
                currentQuery = new BtrieveQuery
                {
                    KeyOffset = LoadedFile.Keys[keyNumber].Segments[0].Offset,
                    KeyDataType = LoadedFile.Keys[keyNumber].Segments[0].DataType,
                    Key = key == null ? null : new byte[key.Length],
                    KeyLength = GetKeyLength(keyNumber)
                };

                /*
                 * Occasionally, zstring keys are marked with a length of 1, where as the length is actually
                 * longer in the defined struct. Because of this, if the key passed in is longer than the definition,
                 * we increase the size of the defined key in the query.
                 */
                if (key != null && key.Length != currentQuery.KeyLength)
                {
                    _logger.Warn($"Adjusting Query Key Size, Data Size {key.Length} differs from Defined Key Size {currentQuery.KeyLength}");
                    currentQuery.KeyLength = (ushort)key.Length;
                }

                /*
                 * TODO -- It appears MajorBBS/WG don't respect the Btrieve length for the key, as it's just part of a struct.
                 * There are modules that define in their btrieve file a STRING key of length 1, but pass in a char*
                 * So for the time being, we just make the key length we're looking for whatever was passed in.
                 */
                if (key != null)
                {
                    Array.Copy(key.ToArray(), 0, currentQuery.Key, 0, key.Length);
                }

                //Update Previous for the next run
                PreviousQuery = currentQuery;
            }
            else
            {
                currentQuery = PreviousQuery;
            }

            return btrieveOperationCode switch
            {
                EnumBtrieveOperationCodes.GetEqual => GetByKeyEqual(currentQuery),
                EnumBtrieveOperationCodes.GetKeyEqual => GetByKeyEqual(currentQuery),

                EnumBtrieveOperationCodes.GetKeyFirst when currentQuery.KeyDataType == EnumKeyDataType.Zstring => GetByKeyFirstAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetKeyFirst when currentQuery.KeyDataType == EnumKeyDataType.String => GetByKeyFirstAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetKeyFirst => GetByKeyFirstNumeric(currentQuery),

                EnumBtrieveOperationCodes.GetFirst when currentQuery.KeyDataType == EnumKeyDataType.Zstring => GetByKeyFirstAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetFirst when currentQuery.KeyDataType == EnumKeyDataType.String => GetByKeyFirstAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetFirst => GetByKeyFirstNumeric(currentQuery),

                EnumBtrieveOperationCodes.GetKeyNext when currentQuery.KeyDataType == EnumKeyDataType.Zstring => GetByKeyNextAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetKeyNext when currentQuery.KeyDataType == EnumKeyDataType.String => GetByKeyNextAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetKeyNext => GetByKeyNextNumeric(currentQuery),

                EnumBtrieveOperationCodes.GetKeyGreater when currentQuery.KeyDataType == EnumKeyDataType.Zstring => GetByKeyGreaterAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetKeyGreater when currentQuery.KeyDataType == EnumKeyDataType.String => GetByKeyGreaterAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetKeyGreater => GetByKeyGreaterNumeric(currentQuery),

                EnumBtrieveOperationCodes.GetGreater when currentQuery.KeyDataType == EnumKeyDataType.Zstring => GetByKeyGreaterAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetGreater when currentQuery.KeyDataType == EnumKeyDataType.String => GetByKeyGreaterAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetGreater => GetByKeyGreaterNumeric(currentQuery),

                EnumBtrieveOperationCodes.GetGreaterOrEqual => GetByKeyGreaterOrEqualNumeric(currentQuery),

                EnumBtrieveOperationCodes.GetLessOrEqual => GetByKeyLessOrEqualNumeric(currentQuery),

                EnumBtrieveOperationCodes.GetLess when currentQuery.KeyDataType == EnumKeyDataType.Zstring => GetByKeyLessAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetLess when currentQuery.KeyDataType == EnumKeyDataType.String => GetByKeyLessAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetLess => GetByKeyLessNumeric(currentQuery),

                EnumBtrieveOperationCodes.GetKeyLess when currentQuery.KeyDataType == EnumKeyDataType.Zstring => GetByKeyLessAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetKeyLess when currentQuery.KeyDataType == EnumKeyDataType.String => GetByKeyLessAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetKeyLess => GetByKeyLessNumeric(currentQuery),

                EnumBtrieveOperationCodes.GetLast when currentQuery.KeyDataType == EnumKeyDataType.Zstring => GetByKeyLastAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetLast when currentQuery.KeyDataType == EnumKeyDataType.String => GetByKeyLastAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetLast => GetByKeyLastNumeric(currentQuery),

                EnumBtrieveOperationCodes.GetKeyLast when currentQuery.KeyDataType == EnumKeyDataType.Zstring => GetByKeyLastAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetKeyLast when currentQuery.KeyDataType == EnumKeyDataType.String => GetByKeyLastAlphabetical(currentQuery),
                EnumBtrieveOperationCodes.GetKeyLast => GetByKeyLastNumeric(currentQuery),

                _ => throw new Exception($"Unsupported Operation Code: {btrieveOperationCode}")
            };
        }

        /// <summary>
        ///     Returns the Record at the specified position
        /// </summary>
        /// <param name="absolutePosition"></param>
        /// <returns></returns>
        public ReadOnlySpan<byte> GetRecordByOffset(uint absolutePosition)
        {
            foreach (var record in LoadedFile.Records)
            {
                if (record.Offset == absolutePosition)
                {
                    Position = record.Offset;
                    return record.Data;
                }
            }

            throw new Exception($"No Btrieve Record located at Offset {absolutePosition}");
        }

        /// <summary>
        ///     Returns the defined Data Length of the specified Key
        /// </summary>
        /// <param name="keyNumber"></param>
        /// <returns></returns>
        public ushort GetKeyLength(ushort keyNumber) => (ushort)LoadedFile.Keys[keyNumber].Segments.Sum(x => x.Length);

        /// <summary>
        ///     Returns the defined Key Type of the specified Key
        /// </summary>
        /// <param name="keyNumber"></param>
        /// <returns></returns>
        public EnumKeyDataType GetKeyType(ushort keyNumber) => LoadedFile.Keys[keyNumber].Segments[0].DataType;

        /// <summary>
        ///     Retrieves the First Record, numerically, by the specified key
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyFirstAlphabetical(BtrieveQuery query)
        {
            //Since we're doing FIRST, we want to search all available records
            var recordsToSearch = LoadedFile.Records.OrderBy(x => x.Offset);
            var lowestRecordOffset = uint.MaxValue;
            var lowestRecordKeyValue = "ZZZZZZZZZZZZZZZZZ"; //hacky way of ensuring the first record we find is before this alphabetically :P

            //Loop through each record
            foreach (var r in recordsToSearch)
            {
                var recordKeyValue = Encoding.ASCII.GetString(r.ToSpan().Slice(query.KeyOffset, query.KeyLength)).TrimEnd('\0');

                //Compare the value of the key, we're looking for the lowest
                //If it's lower than the lowest we've already compared, save the offset
                if (string.Compare(recordKeyValue, lowestRecordKeyValue) < 0)
                {
                    lowestRecordOffset = r.Offset;
                    lowestRecordKeyValue = recordKeyValue;
                }
            }

            //No first??? Throw 0
            if (lowestRecordOffset == uint.MaxValue)
            {
                _logger.Warn($"Unable to locate First record by key {LoadedFileName}:{query.KeyOffset}:{query.KeyLength}");
                return 0;
            }
#if DEBUG
            _logger.Info($"Offset set to {lowestRecordOffset}");
#endif
            Position = (uint)lowestRecordOffset;
            return 1;
        }

        /// <summary>
        ///     Retrieves the First Record, numerically, by the specified key
        /// </summary>
        /// <param name="query"></param>
        private ushort GetByKeyFirstNumeric(BtrieveQuery query)
        {
            //Since we're doing FIRST, we want to search all available records
            var recordsToSearch = LoadedFile.Records.OrderBy(x => x.Offset);
            var lowestRecordOffset = uint.MaxValue;
            var lowestRecordKeyValue = uint.MaxValue;

            //Loop through each record
            foreach (var r in recordsToSearch)
            {
                var recordKey = r.ToSpan().Slice(query.KeyOffset, query.KeyLength);
                uint recordKeyValue = 0;

                switch (query.KeyLength)
                {
                    case 0: //null -- so just treat it as 16-bit
                        recordKeyValue = 0;
                        break;
                    case 2:
                        recordKeyValue = BitConverter.ToUInt16(recordKey);
                        break;
                    case 4:
                        recordKeyValue = BitConverter.ToUInt32(recordKey);
                        break;
                }

                //Compare the value of the key, we're looking for the lowest
                //If it's lower than the lowest we've already compared, save the offset
                if (recordKeyValue < lowestRecordKeyValue)
                {
                    lowestRecordOffset = r.Offset;
                    lowestRecordKeyValue = recordKeyValue;
                }
            }

            //No first??? Throw 0
            if (lowestRecordOffset == uint.MaxValue)
            {
                _logger.Warn($"Unable to locate First record by key {LoadedFileName}:{query.KeyOffset}:{query.KeyLength}");
                return 0;
            }
#if DEBUG
            _logger.Info($"Offset set to {lowestRecordOffset}");
#endif
            Position = lowestRecordOffset;
            return 1;
        }

        /// <summary>
        ///     GetNext for Non-Numeric Key Types
        ///
        ///     Search for the next logical record with a matching Key that comes after the current Position
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyNextAlphabetical(BtrieveQuery query)
        {
            //Searching All Records AFTER current for the given key
            var recordsToSearch = LoadedFile.Records.Where(x => x.Offset > Position).OrderBy(x => x.Offset);

            foreach (var r in recordsToSearch)
            {
                var recordKey = r.ToSpan().Slice(query.KeyOffset, query.KeyLength);

                if (recordKey.SequenceEqual(query.Key))
                {
                    Position = r.Offset;
                    return 1;
                }
            }

            //Unable to find record with matching key value
            return 0;
        }

        /// <summary>
        ///     GetNext for Numeric key types
        ///
        ///     Key Value needs to be incremented, then the specific key needs to be found
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyNextNumeric(BtrieveQuery query)
        {
            //Set Query Value to Current Value
            query.Key = (new ReadOnlySpan<byte>(GetRecord()).Slice(query.KeyOffset, query.KeyLength)).ToArray();

            //Increment the Value & Save It
            switch (query.KeyLength)
            {
                case 2:
                    query.Key = BitConverter.GetBytes((ushort)(BitConverter.ToUInt16(query.Key) + 1));
                    break;
                case 4:
                    query.Key = BitConverter.GetBytes(BitConverter.ToUInt32(query.Key) + 1);
                    break;
                default:
                    throw new Exception($"Unsupported Key Length: {query.KeyLength} ({LoadedFileName})");
            }

            return GetByKeyEqual(query);
        }

        /// <summary>
        ///     Gets the First Logical Record for the given key
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyEqual(BtrieveQuery query)
        {
            //Searching All Records for the Given Key
            var recordsToSearch = LoadedFile.Records.OrderBy(x => x.Offset);

            foreach (var r in recordsToSearch)
            {
                var recordKey = r.ToSpan().Slice(query.KeyOffset, query.KeyLength);

                if (recordKey.SequenceEqual(query.Key))
                {
                    Position = r.Offset;
                    return 1;
                }
            }

            //Unable to find record with matching key value
            return 0;
        }

        /// <summary>
        ///     Retrieves the Next Record, Alphabetically, by the specified key
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyGreaterAlphabetical(BtrieveQuery query)
        {
            //Since we're doing FIRST, we want to search all available records
            var recordsToSearch = LoadedFile.Records.Where(x => x.Offset > Position).OrderBy(x => x.Offset);
            var currentRecordKey = Encoding.ASCII.GetString(query.Key).TrimEnd('\0');

            //Loop through each record
            foreach (var r in recordsToSearch)
            {
                var recordKeyValue = Encoding.ASCII.GetString(r.ToSpan().Slice(query.KeyOffset, query.KeyLength)).TrimEnd('\0');

                //Compare the value of the key, we're looking for the lowest
                //If it's lower than the lowest we've already compared, save the offset
                if (string.Compare(recordKeyValue, currentRecordKey) > 0)
                {
                    Position = r.Offset;
                    return 1;
                }
            }

            return 0;
        }

        /// <summary>
        ///     Search for the next logical record after the current position with a Key value that is Greater Than or Equal To the specified key
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyGreaterOrEqualNumeric(BtrieveQuery query)
        {
            //Only searching Records AFTER the current one in logical order
            var recordsToSearch = LoadedFile.Records.Where(x => x.Offset > Position).OrderBy(x => x.Offset);
            uint queryKeyValue = 0;

            //Get the Query Key Value
            switch (query.KeyLength)
            {
                case 0: //null -- so just treat it as 16-bit
                    queryKeyValue = 0;
                    break;
                case 2:
                    queryKeyValue = BitConverter.ToUInt16(query.Key);
                    break;
                case 4:
                    queryKeyValue = BitConverter.ToUInt32(query.Key);
                    break;
            }

            foreach (var r in recordsToSearch)
            {
                var recordKey = r.ToSpan().Slice(query.KeyOffset, query.KeyLength);
                uint recordKeyValue = 0;

                switch (query.KeyLength)
                {
                    case 0: //null -- so just treat it as 16-bit
                        recordKeyValue = 0;
                        break;
                    case 2:
                        recordKeyValue = BitConverter.ToUInt16(recordKey);
                        break;
                    case 4:
                        recordKeyValue = BitConverter.ToUInt32(recordKey);
                        break;
                }

                if (recordKeyValue >= queryKeyValue)
                {
                    Position = r.Offset;
                    return 1;
                }
            }

            return 0;
        }

        /// <summary>
        ///     Search for the next logical record after the current position with a Key value that is Less Than or Equal To the specified key
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyLessOrEqualNumeric(BtrieveQuery query)
        {
            //Only searching Records AFTER the current one in logical order
            var recordsToSearch = LoadedFile.Records.Where(x => x.Offset > Position).OrderBy(x => x.Offset);
            uint queryKeyValue = 0;

            //Get the Query Key Value
            switch (query.KeyLength)
            {
                case 0: //null -- so just treat it as 16-bit
                    queryKeyValue = 0;
                    break;
                case 2:
                    queryKeyValue = BitConverter.ToUInt16(query.Key);
                    break;
                case 4:
                    queryKeyValue = BitConverter.ToUInt32(query.Key);
                    break;
            }

            foreach (var r in recordsToSearch)
            {
                var recordKey = r.ToSpan().Slice(query.KeyOffset, query.KeyLength);
                uint recordKeyValue = 0;

                switch (query.KeyLength)
                {
                    case 0: //null -- so just treat it as 16-bit
                        recordKeyValue = 0;
                        break;
                    case 2:
                        recordKeyValue = BitConverter.ToUInt16(recordKey);
                        break;
                    case 4:
                        recordKeyValue = BitConverter.ToUInt32(recordKey);
                        break;
                }

                if (recordKeyValue <= queryKeyValue)
                {
                    Position = r.Offset;
                    return 1;
                }
            }

            return 0;
        }

        /// <summary>
        ///     Search for the next logical record after the current position with a Key value that is Greater Than the specified key
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyGreaterNumeric(BtrieveQuery query)
        {
            //Only searching Records AFTER the current one in logical order
            var recordsToSearch = LoadedFile.Records.Where(x => x.Offset > Position).OrderBy(x => x.Offset);
            uint queryKeyValue = 0;

            //Get the Query Key Value
            switch (query.KeyLength)
            {
                case 0: //null -- so just treat it as 16-bit
                    queryKeyValue = 0;
                    break;
                case 2:
                    queryKeyValue = BitConverter.ToUInt16(query.Key);
                    break;
                case 4:
                    queryKeyValue = BitConverter.ToUInt32(query.Key);
                    break;
            }

            foreach (var r in recordsToSearch)
            {
                var recordKey = r.ToSpan().Slice(query.KeyOffset, query.KeyLength);
                uint recordKeyValue = 0;

                switch (query.KeyLength)
                {
                    case 0: //null -- so just treat it as 16-bit
                        recordKeyValue = 0;
                        break;
                    case 2:
                        recordKeyValue = BitConverter.ToUInt16(recordKey);
                        break;
                    case 4:
                        recordKeyValue = BitConverter.ToUInt32(recordKey);
                        break;
                }

                if (recordKeyValue > queryKeyValue)
                {
                    Position = r.Offset;
                    return 1;
                }
            }

            return 0;
        }

        /// <summary>
        ///     Retrieves the Next Record, Alphabetically, by the specified key
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyLessAlphabetical(BtrieveQuery query)
        {
            //Since we're doing FIRST, we want to search all available records
            var recordsToSearch = LoadedFile.Records.Where(x => x.Offset > Position).OrderBy(x => x.Offset);
            var currentRecordKey = Encoding.ASCII.GetString(query.Key).TrimEnd('\0');

            //Loop through each record
            foreach (var r in recordsToSearch)
            {
                var recordKeyValue = Encoding.ASCII.GetString(r.ToSpan().Slice(query.KeyOffset, query.KeyLength)).TrimEnd('\0');

                //Compare the value of the key, we're looking for the lowest
                //If it's lower than the lowest we've already compared, save the offset
                if (string.Compare(recordKeyValue, currentRecordKey) < 0)
                {
                    Position = r.Offset;
                    return 1;
                }
            }

            return 0;
        }

        /// <summary>
        ///     Search for the next logical record after the current position with a Key value that is Less Than the specified key
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyLessNumeric(BtrieveQuery query)
        {
            //Only searching Records AFTER the current one in logical order
            var recordsToSearch = LoadedFile.Records.Where(x => x.Offset > Position).OrderBy(x => x.Offset);
            uint queryKeyValue = 0;

            //Get the Query Key Value
            switch (query.KeyLength)
            {
                case 0: //null -- so just treat it as 16-bit
                    queryKeyValue = 0;
                    break;
                case 2:
                    queryKeyValue = BitConverter.ToUInt16(query.Key);
                    break;
                case 4:
                    queryKeyValue = BitConverter.ToUInt32(query.Key);
                    break;
            }

            foreach (var r in recordsToSearch)
            {
                var recordKey = r.ToSpan().Slice(query.KeyOffset, query.KeyLength);
                uint recordKeyValue = 0;

                switch (query.KeyLength)
                {
                    case 0: //null -- so just treat it as 16-bit
                        recordKeyValue = 0;
                        break;
                    case 2:
                        recordKeyValue = BitConverter.ToUInt16(recordKey);
                        break;
                    case 4:
                        recordKeyValue = BitConverter.ToUInt32(recordKey);
                        break;
                }

                if (recordKeyValue < queryKeyValue)
                {
                    Position = r.Offset;
                    return 1;
                }
            }

            return 0;
        }

        /// <summary>
        ///     Retrieves the Last Record, Alphabetically, by the specified key
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyLastAlphabetical(BtrieveQuery query)
        {
            //Since we're doing FIRST, we want to search all available records
            var recordsToSearch = LoadedFile.Records.OrderBy(x => x.Offset);
            var lowestRecordOffset = uint.MaxValue;
            var lowestRecordKeyValue = "\0"; //hacky way of ensuring the first record we find is before this alphabetically :P

            //Loop through each record
            foreach (var r in recordsToSearch)
            {
                var recordKeyValue = Encoding.ASCII.GetString(r.ToSpan().Slice(query.KeyOffset, query.KeyLength)).TrimEnd('\0');

                //Compare the value of the key, we're looking for the lowest
                //If it's lower than the lowest we've already compared, save the offset
                if (string.Compare(recordKeyValue, lowestRecordKeyValue) > 0)
                {
                    lowestRecordOffset = r.Offset;
                    lowestRecordKeyValue = recordKeyValue;
                }
            }

            //No first??? Throw 0
            if (lowestRecordOffset == uint.MaxValue)
            {
                _logger.Warn($"Unable to locate Last record by key {LoadedFileName}:{query.KeyOffset}:{query.KeyLength}");
                return 0;
            }
#if DEBUG
            _logger.Info($"Offset set to {lowestRecordOffset}");
#endif
            Position = lowestRecordOffset;
            return 1;
        }

        /// <summary>
        ///     Search for the Last logical record after the current position with the specified Key value
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        private ushort GetByKeyLastNumeric(BtrieveQuery query)
        {
            //Since we're doing LAST, we want to search all available records
            var recordsToSearch = LoadedFile.Records.OrderBy(x => x.Offset);
            var highestRecordOffset = uint.MaxValue;
            var highestRecordKeyValue = -1;

            //Loop through each record
            foreach (var r in recordsToSearch)
            {
                var recordKey = r.ToSpan().Slice(query.KeyOffset, query.KeyLength);
                uint recordKeyValue = 0;

                switch (query.KeyLength)
                {
                    case 0: //null -- so just treat it as 16-bit
                        recordKeyValue = 0;
                        break;
                    case 2:
                        recordKeyValue = BitConverter.ToUInt16(recordKey);
                        break;
                    case 4:
                        recordKeyValue = BitConverter.ToUInt32(recordKey);
                        break;
                }

                //Compare the value of the key, we're looking for the lowest
                //If it's lower than the lowest we've already compared, save the offset
                if (recordKeyValue > highestRecordKeyValue)
                {
                    highestRecordOffset = r.Offset;
                    highestRecordKeyValue = (int)recordKeyValue;
                }
            }

            //No first??? Throw 0
            if (highestRecordOffset == uint.MaxValue)
                return 0;
#if DEBUG
            _logger.Info($"Offset set to {highestRecordOffset}");
#endif
            Position = highestRecordOffset;
            return 1;
        }

        /// <summary>
        ///     Writes a similar file as BUTIL -RECOVER to verify records loaded from recovery process match records loaded by MBBSEmu
        /// </summary>
        /// <param name="file"></param>
        /// <param name="records"></param>
        public static void WriteBtrieveRecoveryFile(string file, IEnumerable<BtrieveRecord> records)
        {
            using var outputFile = File.Open(file, FileMode.Create);

            foreach (var r in records)
            {
                outputFile.Write(Encoding.ASCII.GetBytes($"{r.Data.Length},"));
                outputFile.Write(r.Data);
                outputFile.Write(new byte[] { 0xD, 0xA });
            }
            outputFile.WriteByte(0x1A);
            outputFile.Close();
        }

        private void CreateSqliteDataTable(SQLiteConnection connection)
        {
            StringBuilder sb = new StringBuilder("CREATE TABLE data_t(id INTEGER PRIMARY KEY, data BLOB NOT NULL");
            foreach(var key in LoadedFile.Keys)
            {
                var segment = key.Value.Segments[0];
                sb.Append($", key{segment.Number} BLOB NOT NULL");
            }
            sb.Append(");");

            using var cmd = new SQLiteCommand(sb.ToString(), connection);
            cmd.ExecuteNonQuery();

            using var transaction = connection.BeginTransaction();
            foreach (var record in LoadedFile.Records.OrderBy(x => x.Offset))
            {
                using var insertCmd = new SQLiteCommand(connection);

                sb = new StringBuilder("INSERT INTO data_t(data");
                foreach(var key in LoadedFile.Keys)
                {
                    var segment = key.Value.Segments[0];
                    sb.Append($", key{segment.Number}");
                }
                sb.Append(") VALUES(@data");
                foreach(var key in LoadedFile.Keys)
                {
                    var segment = key.Value.Segments[0];
                    sb.Append($", @key{segment.Number}");
                }
                sb.Append(");");
                insertCmd.CommandText = sb.ToString();

                //var data = record.Data;
                insertCmd.Parameters.AddWithValue("@data", record.Data);
                foreach(var key in LoadedFile.Keys)
                {
                    var segment = key.Value.Segments[0];
                    var keyData = record.Data.AsSpan().Slice(segment.Offset, segment.Length);
                    insertCmd.Parameters.AddWithValue($"key{segment.Number}", keyData.ToArray());
                }
                insertCmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        private void CreateSqliteMetadataTable(SQLiteConnection connection)
        {
            string statement = "CREATE TABLE metadata_t(record_length INTEGER NOT NULL, physical_record_length INTEGER NOT NULL, page_length INTEGER NOT NULL)";

            using var cmd = new SQLiteCommand(statement, connection);
            cmd.ExecuteNonQuery();

            using var insertCmd = new SQLiteCommand(connection);
            cmd.CommandText = "INSERT INTO metadata_t(record_length, physical_record_length, page_length) VALUES(@record_length, @physical_record_length, @page_length)";
            cmd.Parameters.AddWithValue("@record_length", LoadedFile.RecordLength);
            cmd.Parameters.AddWithValue("@physical_record_length", LoadedFile.PhysicalRecordLength);
            cmd.Parameters.AddWithValue("@page_length", LoadedFile.PageLength);
            cmd.ExecuteNonQuery();
        }

        private void CreateSqliteKeysTable(SQLiteConnection connection)
        {
            string statement = "CREATE TABLE keys_t(id INTEGER PRIMARY KEY, attributes INTEGER NOT NULL, data_type INTEGER NOT NULL, offset INTEGER NOT NULL, length INTEGER NOT NULL)";

            using var cmd = new SQLiteCommand(statement, connection);
            cmd.ExecuteNonQuery();

            using var insertCmd = new SQLiteCommand(connection);
            cmd.CommandText = "INSERT INTO keys_t(id, attributes, data_type, offset, length) VALUES(@id, @attributes, @data_type, @offset, @length)";

            foreach (var key in LoadedFile.Keys)
            {
                // only grab the first
                var segment = key.Value.Segments[0];
                {
                    cmd.Reset();
                    cmd.Parameters.AddWithValue("@id", segment.Number);
                    cmd.Parameters.AddWithValue("@attributes", segment.Attributes);
                    cmd.Parameters.AddWithValue("@data_type", segment.DataType);
                    cmd.Parameters.AddWithValue("@offset", segment.Offset);
                    cmd.Parameters.AddWithValue("@length", segment.Length);

                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void CreateSqliteDB(string filepath)
        {
            _logger.Warn($"Creating sqlite db {filepath}");

            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder()
            {
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                DataSource = filepath,
            }.ToString();
            string stm = "SELECT SQLITE_VERSION()";

            using var conn = new SQLiteConnection(connectionString);
            conn.Open();

            CreateSqliteMetadataTable(conn);
            CreateSqliteKeysTable(conn);
            CreateSqliteDataTable(conn);

            using var cmd = new SQLiteCommand(stm, conn);
            string version = cmd.ExecuteScalar().ToString();

            _logger.Error($"SQLite version: {version}");

            conn.Close();
        }
    }
}
