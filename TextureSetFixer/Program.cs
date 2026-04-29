using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using System.Threading.Tasks;
using Mutagen.Bethesda.Archives;
using nifly;
using System.IO;
using Noggog;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim.Assets;
using Mutagen.Bethesda.Plugins.Order;

namespace TextureSetFixer
{
    // In a model's texture sets:
    //  3D name is the name of the BSTriShape
    //  3D index is the index of the BSTriShape relative to other BSTriShapes, 0 indexed
    //  i.e, the nth BSTriShape in a model will have a 3D index of n - 1
    // The game uses a texture sets 3D index, even if it doesn't correspond to the same 3D name

    using ModelLink = AssetLinkGetter<SkyrimModelAssetType>;
    using ModelVfs = Dictionary<string, Lazy<NifFile>>;

    public class Program
    {
        public static readonly int NotFound = -1;

        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "TextureSetFixer.esp")
                .Run(args);
        }

        public static NifFile LoadNif(byte[] bytes)
        {
            vectoruchar vector = new(bytes);
            var nif = new NifFile(vector);
            if (!nif.IsValid())
                throw new InvalidOperationException("Nif file is invalid");

            return nif;
        }

        /// <summary>
        /// Build a VFS of all models in the load order
        /// Loading is deferred until the model is actually used
        /// Loose files are prioritized over archives
        /// Paths are case insensitive and relative to the data folder
        /// </summary>
        public static ModelVfs BuildModelVfs(ILoadOrderGetter<IModListing<ISkyrimModGetter>> loadOrder, string dataFolderPath)
        {
            // Archives. Keep last
            ModelVfs vfs = Archive.GetApplicableArchivePaths(GameRelease.SkyrimSE, dataFolderPath, loadOrder.Select(mod => mod.Value.ModKey.FileName))
                .Select(path => Archive.CreateReader(GameRelease.SkyrimSE, path))
                .SelectMany(archive => archive.Files)
                .Reverse()
                .Where(file => file.Path.EndsWith(".nif"))
                .DistinctBy(file => file.Path)
                .ToDictionary(file => new ModelLink(file.Path).DataRelativePath, file => new Lazy<NifFile>(() => LoadNif(file.GetBytes())), StringComparer.OrdinalIgnoreCase);

            // Loose files
            foreach (var file in Directory.EnumerateFiles(dataFolderPath, "*.nif", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dataFolderPath, file);

                vfs[new ModelLink(relative).DataRelativePath] = new(() => LoadNif(File.ReadAllBytes(file)));
            }

            return vfs;
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            Console.WriteLine("Building VFS for models");
            var vfs = BuildModelVfs(state.LoadOrder, state.DataFolderPath);

            Console.WriteLine("Processing");

            bool IsFromPatition(string textureSetShapeName, string nifShapeName)
            {
                if (!nifShapeName.StartsWith(textureSetShapeName))
                    return false;

                // Allow anything but a number after the texture set name
                // E.g., with Lux version of farmintdoorway01.nif, original 3d name FarmIntDoorway01:1
                // FarmIntDoorwar01:01.001 (walls) would be accepted. FarmIntDoorwar01:015.001 (roof) would not
                // These are likely to come from different shapes
                char nextChar = nifShapeName[textureSetShapeName.Length];
                if (nextChar >= '0' && nextChar <= '9')
                    return false;

                return true;
            }

            // Fix the model if needed
            // Returns true if the model was edited
            bool FixModel(IModel model)
            {
                if (model?.AlternateTextures == null)
                    return false;

                var nif = vfs[model.File.DataRelativePath].Value;
                var indexTo3dName = nif.GetShapeNames();

                bool edited = false;

                // This loop may add to AlternateTextures if the model was partitioned
                var originalAltTextures = model.AlternateTextures.ToList();
                foreach (var altTexture in originalAltTextures)
                {
                    int actualIndex = indexTo3dName.IndexOf(altTexture.Name);
                    // Index changed
                    if (actualIndex != NotFound && actualIndex != altTexture.Index)
                    {
                        edited = true;
                        altTexture.Index = actualIndex;
                    }
                    // Mesh has been partitioned
                    else if (actualIndex == NotFound && indexTo3dName.Any(shapeName => IsFromPatition(altTexture.Name, shapeName)))
                    {
                        edited = true;
                        model.AlternateTextures.Remove(altTexture);

                        for (int index = 0; index < indexTo3dName.Count; index++)
                        {
                            string shapeName = indexTo3dName[index];
                            if (IsFromPatition(altTexture.Name, shapeName))
                            {
                                model.AlternateTextures.Add(new AlternateTexture()
                                {
                                    Name = shapeName,
                                    Index = index,
                                    NewTexture = altTexture.NewTexture,
                                });
                            }
                        }
                        //Console.WriteLine($"NO SHAPE NAME: Model '{modeled.Model.File}' used by '{major.EditorID}' has no shape named '{altTexture.Name}'");
                    }
                    // TODO: Warn about or remove non existent texture sets
                }

                return edited;
            }

            foreach (var record in state.LoadOrder.PriorityOrder.SkyrimMajorRecord().WinningContextOverrides(state.LinkCache))
            {
                if (record.Record is not IModeledGetter modelled)
                    continue;
                if (modelled.Model == null)
                    continue;

                try
                {
                    var modelCopy = modelled.Model.DeepCopy();
                    if (FixModel(modelCopy))
                    {
                        var recordCopy = (IModeled)record.GetOrAddAsOverride(state.PatchMod);
                        recordCopy.Model = modelCopy;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        }
    }
}
