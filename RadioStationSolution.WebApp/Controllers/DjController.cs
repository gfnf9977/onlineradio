using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnlineRadioStation.Domain;
using OnlineRadioStation.Services;
using OnlineRadioStation.Data;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace RadioStationSolution.WebApp.Controllers
{
    public class DjController : Controller
    {
        private readonly IStationService _stationService;
        private readonly IUserService _userService;
        private readonly ITrackRepository _trackRepo;
        private readonly IPlaybackQueueRepository _queueRepo;
        private readonly IAudioProcessor _audioProcessor;
        private readonly LiveStreamService _liveService;

        public DjController(
            IStationService stationService,
            IUserService userService,
            ITrackRepository trackRepo,
            IPlaybackQueueRepository queueRepo,
            IAudioProcessor audioProcessor,
            LiveStreamService liveService)
        {
            _stationService = stationService;
            _userService = userService;
            _trackRepo = trackRepo;
            _queueRepo = queueRepo;
            _audioProcessor = audioProcessor;
            _liveService = liveService;
        }

        private async Task<User?> GetCurrentUserAsync()
        {
            var userIdStr = HttpContext.Session.GetString("CurrentUserId");
            if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out Guid userId))
                return null;

            return await _userService.GetUserByIdAsync(userId);
        }

        [HttpGet]
        public async Task<IActionResult> Upload()
        {
            var djUser = await GetCurrentUserAsync();
            if (djUser == null)
                return Content("У системі немає Діджеїв.");
            if (djUser.AssignedStationId == null)
                return Content("Ваш акаунт ще не прив'язаний до жодної радіостанції. Зверніться до Адміністратора.");

            var station = await _stationService.GetStationByIdAsync(djUser.AssignedStationId.Value);
            ViewBag.StationName = station?.StationName ?? "Невідома станція";
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile trackFile, string title)
        {
            if (trackFile == null || trackFile.Length == 0)
                ModelState.AddModelError("trackFile", "Будь ласка, оберіть MP3-файл.");
            if (string.IsNullOrEmpty(title))
                ModelState.AddModelError("title", "Будь ласка, введіть назву треку.");

            var djUser = await GetCurrentUserAsync();
            if (djUser == null || djUser.AssignedStationId == null)
                return Content("Помилка доступу.");

            if (!ModelState.IsValid)
            {
                var station = await _stationService.GetStationByIdAsync(djUser.AssignedStationId.Value);
                ViewBag.StationName = station?.StationName ?? "Невідома станція";
                return View();
            }

            var tempFileName = $"{Guid.NewGuid()}{Path.GetExtension(trackFile.FileName)}";
            var tempFilePath = Path.Combine(Path.GetTempPath(), tempFileName);

            await using (var stream = new FileStream(tempFilePath, FileMode.Create))
            {
                await trackFile.CopyToAsync(stream);
            }

            try
            {
                await _audioProcessor.ProcessNewTrackAsync(
                    tempFilePath: tempFilePath,
                    title: title,
                    stationId: djUser.AssignedStationId.Value,
                    djId: djUser.UserId
                );
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Помилка обробки треку: {ex.Message}");
                var station = await _stationService.GetStationByIdAsync(djUser.AssignedStationId.Value);
                ViewBag.StationName = station?.StationName ?? "Невідома станція";
                return View();
            }
            finally
            {
                if (System.IO.File.Exists(tempFilePath))
                    System.IO.File.Delete(tempFilePath);
            }

            return RedirectToAction(nameof(ManagePlaylist));
        }

        [HttpGet]
        public async Task<IActionResult> ManageStreams()
        {
            var djUser = await GetCurrentUserAsync();
            if (djUser == null)
                return Content("Діджея не знайдено.");

            var history = await _stationService.GetStreamsByDjAsync(djUser.UserId);
            var activeStream = await _stationService.GetActiveStreamAsync(djUser.UserId);

            var playlist = new List<PlaybackQueue>();
            if (djUser.AssignedStationId != null)
            {
                var station = await _stationService.GetStationWithPlaylistAsync(djUser.AssignedStationId.Value);
                if (station != null)
                {
                    playlist = station.Playbacks.OrderBy(p => p.QueuePosition).ToList();
                }
            }

            ViewBag.CurrentPlaylist = playlist;

            bool isDbStreamLive = activeStream != null;
            bool isMicLive = _liveService.IsBroadcasting;

            ViewBag.IsLive = isDbStreamLive || isMicLive;
            ViewBag.IsMicLive = isMicLive;
            ViewBag.IsDbLive = isDbStreamLive;

            ViewBag.IsRandom = activeStream?.IsRandom ?? false;
            ViewBag.StationId = djUser.AssignedStationId;

            if (activeStream != null)
            {
                ViewBag.StreamStartTime = DateTime.SpecifyKind(activeStream.StartTime, DateTimeKind.Utc).ToString("o");
            }

            return View(history);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStream(string action, bool startRandom = false)
        {
            var djUser = await GetCurrentUserAsync();
            if (djUser == null || djUser.AssignedStationId == null)
                return RedirectToAction(nameof(ManageStreams));

            try
            {
                if (action == "start")
                {
                    await _stationService.StartStreamAsync(djUser.AssignedStationId.Value, djUser.UserId, startRandom);
                }
                else if (action == "stop")
                {
                    await _stationService.StopStreamAsync(djUser.UserId);
                }
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(ManageStreams));
        }

        [HttpGet]
        public async Task<IActionResult> ManagePlaylist()
        {
            var djUser = await GetCurrentUserAsync();
            if (djUser == null || djUser.AssignedStationId == null)
                return Content("Помилка доступу.");

            var station = await _stationService.GetStationWithPlaylistAsync(djUser.AssignedStationId.Value);
            var playlist = station.Playbacks.OrderBy(p => p.QueuePosition).ToList();
            return View(playlist);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveFromQueue(Guid queueId)
        {
            var djUser = await GetCurrentUserAsync();
            if (djUser == null)
                return RedirectToAction(nameof(ManagePlaylist));

            try
            {
                await _queueRepo.DeleteEntity(queueId);
                await _queueRepo.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error removing from queue: {ex.Message}");
            }

            return RedirectToAction(nameof(ManagePlaylist));
        }

        [HttpGet]
        public async Task<IActionResult> Library()
        {
            var djUser = await GetCurrentUserAsync();
            if (djUser == null || djUser.Role != "Dj")
                return RedirectToAction("Login", "Home");

            var stationId = djUser.AssignedStationId;
            var allTracks = await _trackRepo.GetAll()
                .OrderByDescending(t => t.Title)
                .ToListAsync();

            HashSet<Guid> queuedTrackIds = new HashSet<Guid>();
            if (stationId != null)
            {
                var idsInQueue = await _queueRepo.GetAll()
                    .Where(q => q.StationId == stationId.Value)
                    .Select(q => q.TrackId)
                    .ToListAsync();
                queuedTrackIds = new HashSet<Guid>(idsInQueue);
            }

            ViewBag.QueuedTrackIds = queuedTrackIds;
            return View(allTracks);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditTrackTitle(Guid trackId, string newTitle)
        {
            if (string.IsNullOrWhiteSpace(newTitle))
                return RedirectToAction(nameof(Library));

            var track = await _trackRepo.GetById(trackId);
            if (track != null)
            {
                track.Title = newTitle;
                _trackRepo.UpdateEntity(track);
                await _trackRepo.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Library));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddToQueue(Guid trackId)
        {
            var djUser = await GetCurrentUserAsync();
            if (djUser == null || djUser.Role != "Dj")
                return RedirectToAction("Login", "Home");

            if (djUser.AssignedStationId == null)
            {
                TempData["Error"] = "Ваш акаунт не прив'язаний до станції.";
                return RedirectToAction(nameof(Library));
            }

            var stationId = djUser.AssignedStationId.Value;
            var track = await _trackRepo.GetById(trackId);
            if (track == null)
            {
                TempData["Error"] = "Трек не знайдено.";
                return RedirectToAction(nameof(Library));
            }

            var alreadyInQueue = await _queueRepo.GetAll()
                .AnyAsync(q => q.StationId == stationId && q.TrackId == trackId);

            if (alreadyInQueue)
            {
                TempData["Error"] = $"Трек '{track.Title}' вже є у вашому плейлисті!";
                return RedirectToAction(nameof(Library));
            }

            var currentMaxPosition = await _queueRepo.GetAll()
                .Where(q => q.StationId == stationId)
                .Select(q => (int?)q.QueuePosition)
                .MaxAsync() ?? 0;

            var newQueueItem = new PlaybackQueue
            {
                QueueId = Guid.NewGuid(),
                TrackId = trackId,
                StationId = stationId,
                AddedById = djUser.UserId,
                QueuePosition = currentMaxPosition + 1
            };

            _queueRepo.AddEntity(newQueueItem);
            await _queueRepo.SaveChangesAsync();

            TempData["Success"] = $"Трек '{track.Title}' успішно додано до ефіру!";
            return RedirectToAction(nameof(ManagePlaylist));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangeTrackPosition(Guid queueId, string direction)
        {
            var currentItem = await _queueRepo.GetById(queueId);
            if (currentItem == null)
                return RedirectToAction(nameof(ManagePlaylist));

            var djUser = await GetCurrentUserAsync();
            if (djUser == null || djUser.AssignedStationId != currentItem.StationId)
                return Content("Помилка доступу.");

            var activeStream = await _stationService.GetActiveStreamAsync(djUser.UserId);
            if (activeStream != null)
                return RedirectToAction(nameof(ManagePlaylist));

            PlaybackQueue? targetItem = null;
            var stationQueue = _queueRepo.GetAll().Where(q => q.StationId == currentItem.StationId);

            if (direction == "up")
            {
                targetItem = await stationQueue
                    .Where(q => q.QueuePosition < currentItem.QueuePosition)
                    .OrderByDescending(q => q.QueuePosition)
                    .FirstOrDefaultAsync();
            }
            else
            {
                targetItem = await stationQueue
                    .Where(q => q.QueuePosition > currentItem.QueuePosition)
                    .OrderBy(q => q.QueuePosition)
                    .FirstOrDefaultAsync();
            }

            if (targetItem != null)
            {
                (currentItem.QueuePosition, targetItem.QueuePosition) = (targetItem.QueuePosition, currentItem.QueuePosition);
                _queueRepo.UpdateEntity(currentItem);
                _queueRepo.UpdateEntity(targetItem);
                await _queueRepo.SaveChangesAsync();
            }

            return RedirectToAction(nameof(ManagePlaylist));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangeStreamQueuePosition(Guid queueId, string direction)
        {
            var currentItem = await _queueRepo.GetById(queueId);
            if (currentItem == null)
                return RedirectToAction(nameof(ManageStreams));

            var djUser = await GetCurrentUserAsync();
            if (djUser == null || djUser.AssignedStationId != currentItem.StationId)
                return RedirectToAction(nameof(ManageStreams));

            var activeStream = await _stationService.GetActiveStreamAsync(djUser.UserId);
            if (activeStream != null)
                return RedirectToAction(nameof(ManageStreams));

            PlaybackQueue? targetItem = null;
            var stationQueue = _queueRepo.GetAll().Where(q => q.StationId == currentItem.StationId);

            if (direction == "up")
                targetItem = await stationQueue.Where(q => q.QueuePosition < currentItem.QueuePosition).OrderByDescending(q => q.QueuePosition).FirstOrDefaultAsync();
            else
                targetItem = await stationQueue.Where(q => q.QueuePosition > currentItem.QueuePosition).OrderBy(q => q.QueuePosition).FirstOrDefaultAsync();

            if (targetItem != null)
            {
                (currentItem.QueuePosition, targetItem.QueuePosition) = (targetItem.QueuePosition, currentItem.QueuePosition);
                _queueRepo.UpdateEntity(currentItem);
                _queueRepo.UpdateEntity(targetItem);
                await _queueRepo.SaveChangesAsync();
            }

            return RedirectToAction(nameof(ManageStreams));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleTrackActive(Guid queueId)
        {
            var item = await _queueRepo.GetById(queueId);
            if (item == null)
                return RedirectToAction(nameof(ManageStreams));

            var djUser = await GetCurrentUserAsync();
            if (djUser == null || djUser.AssignedStationId != item.StationId)
                return RedirectToAction(nameof(ManageStreams));

            var activeStream = await _stationService.GetActiveStreamAsync(djUser.UserId);
            if (activeStream != null)
                return RedirectToAction(nameof(ManageStreams));

            item.IsActive = !item.IsActive;
            _queueRepo.UpdateEntity(item);
            await _queueRepo.SaveChangesAsync();

            return RedirectToAction(nameof(ManageStreams));
        }
    }
}