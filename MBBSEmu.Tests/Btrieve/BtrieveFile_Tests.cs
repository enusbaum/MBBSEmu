using FluentAssertions;
using FluentAssertions.Extensions;
using MBBSEmu.Btrieve;
using MBBSEmu.Btrieve.Enums;
using MBBSEmu.DependencyInjection;
using MBBSEmu.Resources;
using NLog;
using System.IO;
using System;
using Xunit;

namespace MBBSEmu.Tests.Btrieve
{
    public class BtrieveFile_Tests : TestBase, IDisposable
    {
        private static readonly Random RANDOM = new Random();

        private readonly string[] _btrieveFiles = {"MBBSEMU.DAT"};


        protected readonly string _modulePath = Path.Join(Path.GetTempPath(), $"mbbsemu{RANDOM.Next()}");
        public BtrieveFile_Tests()
        {
            Directory.CreateDirectory(_modulePath);

            CopyFilesToTempPath(ResourceManager.GetTestResourceManager());
        }

        public void Dispose()
        {
            Directory.Delete(_modulePath,  recursive: true);
        }

        private void CopyFilesToTempPath(IResourceManager resourceManager)
        {
            foreach (var file in _btrieveFiles)
            {
                File.WriteAllBytes(Path.Combine(_modulePath, file), resourceManager.GetResource($"MBBSEmu.Tests.Assets.{file}").ToArray());
            }
        }

        [Fact]
        public void LoadsFile()
        {
            ServiceResolver serviceResolver = new ServiceResolver(ServiceResolver.GetTestDefaults());

            var btrieve = new BtrieveFile();
            btrieve.LoadFile(serviceResolver.GetService<ILogger>(), _modulePath, "MBBSEMU.DAT");

            Assert.Equal(3, btrieve.KeyCount);
            Assert.Equal(3, btrieve.Keys.Count);
            Assert.Equal(70, btrieve.RecordLength);
            Assert.Equal(86, btrieve.PhysicalRecordLength);
            Assert.Equal(512, btrieve.PageLength);
            Assert.Equal(4, btrieve.PageCount);
            Assert.False(btrieve.LogKeyPresent);

            Assert.Single(btrieve.Keys[0].Segments);
            Assert.Single(btrieve.Keys[1].Segments);
            Assert.Single(btrieve.Keys[2].Segments);


            btrieve.Keys[0].Segments[0].Should().BeEquivalentTo(
                new BtrieveKeyDefinition() {
                    Number = 0,
                    Attributes = EnumKeyAttributeMask.Duplicates,
                    DataType = EnumKeyDataType.Zstring,
                    Offset = 2,
                    Length = 32,
                    Segment = false,
                });
            btrieve.Keys[1].Segments[0].Should().BeEquivalentTo(
                new BtrieveKeyDefinition() {
                    Number = 1,
                    Attributes = EnumKeyAttributeMask.Modifiable,
                    DataType = EnumKeyDataType.Integer,
                    Offset = 34,
                    Length = 4,
                    Segment = false,
                });
            btrieve.Keys[2].Segments[0].Should().BeEquivalentTo(
                new BtrieveKeyDefinition() {
                    Number = 2,
                    Attributes = EnumKeyAttributeMask.Duplicates | EnumKeyAttributeMask.Modifiable,
                    DataType = EnumKeyDataType.Zstring,
                    Offset = 38,
                    Length = 32,
                    Segment = false,
                });
        }
    }
}
