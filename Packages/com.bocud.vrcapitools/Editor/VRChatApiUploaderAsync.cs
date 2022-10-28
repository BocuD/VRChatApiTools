using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using Debug = UnityEngine.Debug;

namespace BocuD.VRChatApiTools
{
    public class VRChatApiUploaderAsync
    {
        public delegate void SetStatusFunc(string header, string status = null, string subStatus = null);
        public delegate void SetUploadProgressFunc(long done, long total);
        public delegate void SetUploadStateFunc(VRChatApiToolsUploadStatus.UploadState state);
        public delegate void SetErrorStateFunc(string header, string details);
        public delegate void LoggerFunc(string contents);

        public VRChatApiToolsUploadStatus uploadStatus;
        
        public SetStatusFunc OnStatus = (header, status, subStatus) => { };
        public SetUploadProgressFunc OnUploadProgress = (done, total) => { };
        public SetUploadStateFunc OnUploadState = state => { };
        public SetErrorStateFunc OnError = (header, details) => { Debug.LogError($"{header}: {details}"); };
        public LoggerFunc Log = contents => Logger.Log(contents);
        public LoggerFunc LogWarning = contents => Logger.LogWarning(contents);
        public LoggerFunc LogError = contents => Logger.LogError(contents);

        public Func<bool> cancelQuery = () => false;

        public void UseStatusWindow()
        {
            uploadStatus = VRChatApiToolsUploadStatus.GetNew();
            
            OnStatus = uploadStatus.SetStatus;
            OnUploadProgress = uploadStatus.SetUploadProgress;
            OnUploadState = uploadStatus.SetUploadState;
            OnError = uploadStatus.SetErrorState;
            cancelQuery = () => uploadStatus.cancelRequested;
        }

        public async Task<bool> UpdateBlueprintImage(ApiModel blueprint, Texture2D newImage)
        {
            if (!(blueprint is ApiAvatar) && !(blueprint is ApiWorld))
                return false;
            
            string newImagePath = SaveImageTemp(newImage);
            
            if (blueprint is ApiWorld world)
            {
                world.imageUrl = await UploadImage(world, newImagePath);
            }
            else if (blueprint is ApiAvatar avatar)
            {
                avatar.imageUrl = await UploadImage(avatar, newImagePath);
            }
            
            bool success = await ApplyBlueprintChanges(blueprint);

            if (success)
                OnUploadState(VRChatApiToolsUploadStatus.UploadState.finished);
            else OnUploadState(VRChatApiToolsUploadStatus.UploadState.failed);

            return success;
        }

        public async Task<bool> ApplyBlueprintChanges(ApiModel blueprint)
        {
            if (!(blueprint is ApiAvatar) && !(blueprint is ApiWorld))
                return false;

            bool doneUploading = false;
            bool success = false;

            OnStatus("Applying Blueprint Changes");
            
            blueprint.Save(
                c =>
                {
                    if (blueprint is ApiAvatar) AnalyticsSDK.AvatarUploaded(blueprint, true);
                    else AnalyticsSDK.WorldUploaded(blueprint, true);
                    doneUploading = true;
                    success = true;
                },
                c =>
                {
                    OnError("Applying blueprint changes failed", c.Error);
                    doneUploading = true;
                });

            while (!doneUploading)
                await Task.Delay(33);

            return success;
        }

        /// <summary>
        /// Upload a World to VRChat
        /// </summary>
        /// <param name="assetBundlePath">World AssetBundle path</param>
        /// <param name="unityPackagePath">UnityPackage path (can be left empty)</param>
        /// <param name="worldInfo">Data structure containing name, description, etc</param>
        /// <returns>blueprint ID of the uploaded world</returns>
        public async Task<string> UploadWorld(string assetBundlePath, string unityPackagePath, VRChatApiTools.WorldInfo worldInfo)
        {
            if (string.IsNullOrWhiteSpace(assetBundlePath))
                throw new Exception("Invalid null or empty AssetBundle path provided");
            
            VRChatApiTools.ClearCaches();
            
            await Task.Delay(100);
            
            if (!await VRChatApiTools.TryAutoLoginAsync()) 
                throw new Exception("Failed to login");
            
            PipelineManager pipelineManager = VRChatApiTools.FindPipelineManager();
            if (pipelineManager == null)
                throw new Exception("Couldn't find Pipeline Manager");

            pipelineManager.user = APIUser.CurrentUser;

            bool isUpdate = true;
            bool wait = true;
            
            ApiWorld apiWorld = new ApiWorld
            {
                id = pipelineManager.blueprintId
            };
            
            apiWorld.Fetch(null,
                (c) =>
                {
                    Log("Updating an existing world.");
                    apiWorld = c.Model as ApiWorld;
                    
                    pipelineManager.completedSDKPipeline = !string.IsNullOrEmpty(apiWorld.authorId);
                    EditorUtility.SetDirty(pipelineManager);
                    
                    isUpdate = true;
                    wait = false;
                },
                (c) =>
                {
                    Log("World record not found, creating a new world.");
                    apiWorld = new ApiWorld { capacity = 16 };
                    
                    pipelineManager.completedSDKPipeline = false;
                    EditorUtility.SetDirty(pipelineManager);
                    
                    apiWorld.id = pipelineManager.blueprintId;
                    isUpdate = false;
                    wait = false;
                });

            while (wait) await Task.Delay(100);

            if (apiWorld == null)
                throw new Exception("Couldn't fetch or create world record");

            //Prepare asset bundle
            int version = Mathf.Max(1, apiWorld.version + 1);
            string uploadVrcPath = FormatAssetBundle(assetBundlePath, apiWorld.id, version, VRChatApiTools.CurrentPlatform(), ApiWorld.VERSION);
            
            //Prepare unity package if it exists
            bool shouldUploadUnityPackage = !string.IsNullOrEmpty(unityPackagePath) && File.Exists(unityPackagePath);
            string uploadUnityPackagePath = shouldUploadUnityPackage ? FormatUnityPackage(unityPackagePath, apiWorld.id, version, VRChatApiTools.CurrentPlatform(), ApiWorld.VERSION) : "";
            if (shouldUploadUnityPackage) Logger.LogWarning("Found UnityPackage. Why are you building with future proof publish enabled?");

            //Assign a new blueprint ID if this is a new world
            if (string.IsNullOrEmpty(apiWorld.id))
            {
                pipelineManager.AssignId();
                apiWorld.id = pipelineManager.blueprintId;
            }

            await UploadWorldData(apiWorld, uploadVrcPath, uploadUnityPackagePath, isUpdate, VRChatApiTools.CurrentPlatform(), worldInfo);
            
            return apiWorld.id;
        }
        
        /// <summary>
        /// Upload an Avatar to VRChat
        /// </summary>
        /// <param name="assetBundlePath">Avatar AssetBundle path</param>
        /// <param name="unityPackagePath">UnityPackage path (can be left empty)</param>
        /// <param name="avatarInfo">Data structure containing name, description, etc</param>
        /// <returns>blueprint ID of the uploaded avatar</returns>
        public async Task<string> UploadAvatar(string assetBundlePath, string unityPackagePath, VRChatApiTools.AvatarInfo avatarInfo)
        {
            if (string.IsNullOrWhiteSpace(assetBundlePath))
                throw new Exception("Invalid null or empty AssetBundle path provided");
            
            VRChatApiTools.ClearCaches();
            
            await Task.Delay(100);
            
            if (!await VRChatApiTools.TryAutoLoginAsync()) 
                throw new Exception("Failed to login");

            bool isUpdate = true;
            bool wait = true;
            
            ApiAvatar apiAvatar = new ApiAvatar
            {
                id = avatarInfo.blueprintID
            };
            
            apiAvatar.Fetch(
                (c) =>
                {
                    Log("Updating an existing avatar.");
                    apiAvatar = c.Model as ApiAvatar;

                    isUpdate = true;
                    wait = false;
                },
                (c) =>
                {
                    Log("Avatar record not found, creating a new avatar.");
                    apiAvatar = new ApiAvatar();

                    isUpdate = false;
                    wait = false;
                });

            while (wait) await Task.Delay(100);

            if (apiAvatar == null)
                throw new Exception("Couldn't fetch or create avatar record");

            //Prepare asset bundle
            int version = Mathf.Max(1, apiAvatar.version + 1);
            string uploadVrcPath = FormatAssetBundle(assetBundlePath, apiAvatar.id, version, VRChatApiTools.CurrentPlatform(), ApiAvatar.VERSION);
            
            //Prepare unity package if it exists
            bool shouldUploadUnityPackage = !string.IsNullOrEmpty(unityPackagePath) && File.Exists(unityPackagePath);
            string uploadUnityPackagePath = shouldUploadUnityPackage ? FormatUnityPackage(unityPackagePath, apiAvatar.id, version, VRChatApiTools.CurrentPlatform(), ApiWorld.VERSION) : "";
            if (shouldUploadUnityPackage) Logger.LogWarning("Found UnityPackage. Why are you building with future proof publish enabled?");

            //Assign a new blueprint ID if this is a avatar
            if (string.IsNullOrEmpty(apiAvatar.id))
            {
                //todo: assign this id to pipelineManager
                apiAvatar.id = VRChatApiTools.GenerateBlueprintID<ApiAvatar>();
            }

            await UploadAvatarData(apiAvatar, uploadVrcPath, uploadUnityPackagePath, isUpdate, VRChatApiTools.CurrentPlatform(), avatarInfo);
            
            return apiAvatar.id;
        }

        private async Task UploadWorldData(ApiWorld apiWorld, string assetBundlePath, string unityPackagePath, bool isUpdate, Platform platform, VRChatApiTools.WorldInfo worldInfo)
        {
            // upload unity package
            if (!string.IsNullOrEmpty(unityPackagePath))
            {
                apiWorld.unityPackageUrl = await UploadFile(unityPackagePath,
                    isUpdate ? apiWorld.unityPackageUrl : "",
                    VRChatApiTools.GetFriendlyWorldFileName("Unity package", apiWorld, platform), "Unity package");
            }
            
            // upload asset bundle
            if (!string.IsNullOrEmpty(assetBundlePath))
            {
                apiWorld.assetUrl = await UploadFile(assetBundlePath, isUpdate ? apiWorld.assetUrl : "",
                    VRChatApiTools.GetFriendlyWorldFileName("Asset bundle", apiWorld, platform), "Asset bundle");
            }
            
            if (string.IsNullOrWhiteSpace(apiWorld.assetUrl)) 
            {
                OnStatus("Failed", "Asset bundle upload failed");
                return;
            }

            bool appliedSucces = false;

            if (isUpdate)
                appliedSucces = await UpdateWorldBlueprint(apiWorld, worldInfo);
            else
                appliedSucces = await CreateWorldBlueprint(apiWorld, worldInfo);

            OnUploadState(appliedSucces
                ? VRChatApiToolsUploadStatus.UploadState.finished
                : VRChatApiToolsUploadStatus.UploadState.failed);
        }
        
        private async Task UploadAvatarData(ApiAvatar apiAvatar, string assetBundlePath, string unityPackagePath, bool isUpdate, Platform platform, VRChatApiTools.AvatarInfo avatarInfo)
        {
            // upload unity package
            if (!string.IsNullOrEmpty(unityPackagePath))
            {
                apiAvatar.unityPackageUrl = await UploadFile(unityPackagePath,
                    isUpdate ? apiAvatar.unityPackageUrl : "",
                    VRChatApiTools.GetFriendlyAvatarFileName("Unity package", apiAvatar.id, platform), "Unity package");
            }
            
            // upload asset bundle
            if (!string.IsNullOrEmpty(assetBundlePath))
            {
                apiAvatar.assetUrl = await UploadFile(assetBundlePath, isUpdate ? apiAvatar.assetUrl : "",
                    VRChatApiTools.GetFriendlyAvatarFileName("Asset bundle", apiAvatar.id, platform), "Asset bundle");
            }
            
            if (string.IsNullOrWhiteSpace(apiAvatar.assetUrl)) 
            {
                OnStatus("Failed", "Asset bundle upload failed");
                return;
            }

            bool appliedSucces = false;

            if (isUpdate)
                appliedSucces = await UpdateAvatarBlueprint(apiAvatar, avatarInfo);
            else
                appliedSucces = await CreateAvatarBlueprint(apiAvatar, avatarInfo);

            OnUploadState(appliedSucces
                ? VRChatApiToolsUploadStatus.UploadState.finished
                : VRChatApiToolsUploadStatus.UploadState.failed);
        }
        
        public async Task<bool> UpdateWorldBlueprint(ApiWorld apiWorld, VRChatApiTools.WorldInfo worldInfo = null)
        {
            if (worldInfo != null)
            {
                apiWorld.ApplyBlueprintInfo(worldInfo);

                if (worldInfo.newImagePath != "")
                {
                    string newImageUrl = await UploadImage(apiWorld, worldInfo.newImagePath);
                    apiWorld.imageUrl = newImageUrl;
                }
            }
            
            OnStatus("Applying Blueprint Changes");
            
            bool success = false;
            bool applied = false;

            apiWorld.Save(c =>
            {
                applied = true;
                success = true;
            }, c =>
            {
                applied = true; 
                LogError(c.Error);
                OnError("Applying blueprint changes failed", c.Error);
                success = false;
            });

            while (!applied)
                await Task.Delay(33);

            return success;
        }

        private async Task<bool> UpdateAvatarBlueprint(ApiAvatar apiAvatar, VRChatApiTools.AvatarInfo avatarInfo)
        {
            if (avatarInfo != null)
            {
                apiAvatar.ApplyBlueprintInfo(avatarInfo);

                if (avatarInfo.newImagePath != "")
                {
                    string newImageUrl = await UploadImage(apiAvatar, avatarInfo.newImagePath);
                    apiAvatar.imageUrl = newImageUrl;
                }
            }

            OnStatus("Applying Blueprint Changes");
            
            bool applied = false;
            bool success = false;
            
            apiAvatar.Save(c =>
            {
                applied = true;
                success = true;
            }, c =>
            {
                applied = true;
                LogError(c.Error);
                OnError("Applying blueprint changes failed", c.Error);
                success = false;
            });

            while (!applied)
                await Task.Delay(33);

            return success;
        }

        private async Task<bool> CreateWorldBlueprint(ApiWorld apiWorld, VRChatApiTools.WorldInfo worldInfo)
        {
            bool success = false;

            apiWorld.name = worldInfo.name;
            apiWorld.description = worldInfo.description;
            apiWorld.tags = worldInfo.tags.ToList();
            apiWorld.capacity = worldInfo.capacity;

            if (worldInfo.newImagePath != "")
            {
                apiWorld.imageUrl = await UploadImage(apiWorld, worldInfo.newImagePath);
            }

            if (string.IsNullOrWhiteSpace(apiWorld.imageUrl))
            {
                apiWorld.imageUrl = await UploadImage(apiWorld, SaveImageTemp(new Texture2D(1200, 900)));
            }

            bool applied = false;

            apiWorld.Post(
                (c) =>
                {
                    ApiWorld savedBlueprint = (ApiWorld)c.Model;
                    
                    PipelineManager pipelineManager = VRChatApiTools.FindPipelineManager();
                    if (pipelineManager == null)
                    {
                        pipelineManager.blueprintId = savedBlueprint.id;
                        EditorUtility.SetDirty(pipelineManager);
                    }

                    applied = true;
                    worldInfo.blueprintID = savedBlueprint.id;
                    success = true;
                },
                (c) =>
                {
                    applied = true;
                    Debug.LogError(c.Error);
                    success = false;
                    OnError("Creating blueprint failed", c.Error);
                });

            while (!applied)
                await Task.Delay(100);

            return success;
        }

        private async Task<bool> CreateAvatarBlueprint(ApiAvatar apiAvatar, VRChatApiTools.AvatarInfo avatarInfo)
        {
            bool success = false;

            apiAvatar.name = avatarInfo.name;
            apiAvatar.description = avatarInfo.description;
            apiAvatar.tags = avatarInfo.tags.ToList();
            
            if (avatarInfo.newImagePath != "")
            {
                apiAvatar.imageUrl = await UploadImage(apiAvatar, avatarInfo.newImagePath);
            }
            
            if (string.IsNullOrWhiteSpace(apiAvatar.imageUrl))
            {
                apiAvatar.imageUrl = await UploadImage(apiAvatar, SaveImageTemp(new Texture2D(1200, 900)));
            }

            bool applied = false;
            
            apiAvatar.Post(
                (c) =>
                {
                    ApiWorld savedBlueprint = (ApiWorld)c.Model;
                    
                    //todo: save blueprint id to pipeline manager (this might be different for avatars, need to look into how the avatar sdk pipeline works)
                    // PipelineManager pipelineManager = VRChatApiTools.FindPipelineManager();
                    // if (pipelineManager == null)
                    // {
                    //     pipelineManager.blueprintId = savedBlueprint.id;
                    //     EditorUtility.SetDirty(pipelineManager);
                    // }
                    
                    applied = true;
                    avatarInfo.blueprintID = savedBlueprint.id;
                    success = true;
                },
                (c) =>
                {
                    applied = true;
                    Debug.LogError(c.Error);
                    success = false;
                    OnError("Creating blueprint failed", c.Error);
                });

            while (!applied)
                await Task.Delay(100);

            return success;
        }
        
        #region Image Upload / Tools
        public async Task<string> UploadImage(ApiModel blueprint, string newImagePath)
        {
            string friendlyFileName;
            string existingFileUrl;

            switch (blueprint)
            {
                case ApiWorld world:
                    friendlyFileName = VRChatApiTools.GetFriendlyWorldFileName("Image", world, VRChatApiTools.CurrentPlatform());
                    existingFileUrl = world.imageUrl;
                    break;
                case ApiAvatar avatar:
                    friendlyFileName = VRChatApiTools.GetFriendlyAvatarFileName("Image", avatar.id, VRChatApiTools.CurrentPlatform());
                    existingFileUrl = avatar.imageUrl;
                    break;
                default:
                    throw new ArgumentException("Unsupported ApiModel passed");
            }
            
            Log($"Preparing image upload for {newImagePath}...");
            
            string newUrl = null;

            if (!string.IsNullOrEmpty(newImagePath))
            {
                newUrl = await UploadFile(newImagePath, existingFileUrl, friendlyFileName, "Image");
            }
            
            return newUrl;
        }

        public static string SaveImageTemp(Texture2D input)
        {
            byte[] png = input.EncodeToPNG();
            string path = ImageName(input.width, input.height, "image", Application.temporaryCachePath);
            File.WriteAllBytes(path, png);
            return path;
        }

        private static string ImageName(int width, int height, string name, string savePath) =>
            $"{savePath}/{name}_{width}x{height}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
#endregion

        public async Task<string> UploadFile(string filePath, string existingFileUrl, string friendlyFileName, string fileType)
        {
            string newFileUrl = "";

            if (string.IsNullOrEmpty(filePath))
            {
                LogError("Null file passed to UploadFileAsync");
                return newFileUrl;
            }

            Log($"Uploading {fileType} ({filePath.GetFileName()}) ...");

            OnStatus($"Uploading {fileType}...");

            string fileId = ApiFile.ParseFileIdFromFileAPIUrl(existingFileUrl);

            ApiFileHelperAsync fileHelperAsync = new ApiFileHelperAsync();

            Stopwatch stopwatch = Stopwatch.StartNew();
            
            newFileUrl = await fileHelperAsync.UploadFile(filePath, fileId, fileType, friendlyFileName,
                (status, subStatus) => OnStatus(status, subStatus), (done, total) => OnUploadProgress(done, total),
                cancelQuery);

            Log($"<color=green>{fileType} upload succeeded</color>");
            stopwatch.Stop();
            OnStatus("Upload Succesful", $"Finished upload in {stopwatch.Elapsed:mm\\:ss}");

            return newFileUrl;
        }

        private static string FormatAssetBundle(string assetBundlePath, string blueprintId, int version, Platform platform, AssetVersion assetVersion)
        {
            string uploadVrcPath =
                $"{Application.temporaryCachePath}/{blueprintId}_{version}_{Application.unityVersion}_{assetVersion.ApiVersion}_{platform.ToApiString()}_{API.GetServerEnvironmentForApiUrl()}{Path.GetExtension(assetBundlePath)}";

            if (File.Exists(uploadVrcPath))
                File.Delete(uploadVrcPath);

            File.Copy(assetBundlePath, uploadVrcPath);

            return uploadVrcPath;
        }
        
        private static string FormatUnityPackage(string packagePath, string blueprintId, int version, Platform platform, AssetVersion assetVersion)
        {
            string uploadUnityPackagePath =
                $"{Application.temporaryCachePath}/{blueprintId}_{version}_{Application.unityVersion}_{assetVersion.ApiVersion}_{platform.ToApiString()}_{API.GetServerEnvironmentForApiUrl()}.unitypackage";

            if (File.Exists(uploadUnityPackagePath))
                File.Delete(uploadUnityPackagePath);

            File.Copy(packagePath, uploadUnityPackagePath);

            return uploadUnityPackagePath;
        }
    }
}
