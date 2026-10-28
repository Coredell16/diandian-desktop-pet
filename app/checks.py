"""Offline regression checks; run with QT_QPA_PLATFORM=offscreen."""
import json
import copy
import shutil
import sys
import tempfile
import time
from pathlib import Path

from PyQt5.QtCore import QEvent, QPoint, QPointF, QRectF, QSettings, Qt
from PyQt5.QtGui import QImage, QMouseEvent, QPainter
from PyQt5.QtMultimedia import QSoundEffect
from PyQt5.QtTest import QTest
from PyQt5.QtWidgets import QApplication, QMenu


def run(app):
    from diandian import Diandian, VERSION, ROOT, action_pair, apply_motion, pose_sprite
    from content import asset_path, load_project, save_project, validate_project

    assert app.platformName() == 'offscreen', 'Set QT_QPA_PLATFORM=offscreen before QApplication.'
    output = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else Path.cwd() / 'work'
    output.mkdir(parents=True, exist_ok=True)
    output = output.resolve()
    checked = []
    birds = []

    def wait_for(predicate, message, timeout=5000):
        deadline = time.monotonic() + timeout / 1000
        while not predicate() and time.monotonic() < deadline:
            QTest.qWait(10)
        assert predicate(), message

    def make_bird(ini):
        settings = QSettings(str(ini), QSettings.IniFormat)
        pet = Diandian(settings=settings, content_root=content_root)
        birds.append(pet)
        pet.timer.stop()
        pet.tray.hide()
        pet.next_action = pet.next_call = float('inf')
        pet.show()
        app.processEvents()
        return pet

    class Recorder:
        def __init__(self):
            self.calls = []

        def save(self):
            pass

        def restore(self):
            pass

        def translate(self, *args):
            pass

        def scale(self, *args):
            pass

        def rotate(self, *args):
            pass

        def setRenderHint(self, *args):
            pass

        def drawPixmap(self, *args):
            point, pixmap = args if len(args) == 2 else (QPointF(args[0], args[1]), args[2])
            self.calls.append((point, pixmap.cacheKey()))

    def mouse_move(pet):
        move = QMouseEvent(QEvent.MouseMove, QPointF(110, 120),
                           QPointF(pet.mapToGlobal(QPoint(110, 120))),
                           Qt.NoButton, Qt.LeftButton, Qt.NoModifier)
        QApplication.sendEvent(pet, move)

    def check_builtin_pose_edges(pet):
        # A larger widget can hide clipping that has already happened inside the
        # fixed pose canvas. Check the stock crests, wings and tails themselves.
        indices = list(range(16))
        indices.extend(16 + i for i, frame in enumerate(pet.project['frames'][:8])
                       if Path(frame['file']).name == 'play.png')
        for label, sprites, selected in (('normal', pet.scaled, indices),
                                         ('flight', pet.flight_scaled, range(8))):
            for index in selected:
                image = sprites[index].toImage().convertToFormat(QImage.Format_RGBA8888)
                alpha = image.constBits().asstring(image.byteCount())[3::4]
                width = image.width()
                border = alpha[:width] + alpha[-width:] + alpha[::width] + alpha[width-1::width]
                assert max(border) == 0, ('Silhouette clipped inside pose canvas',
                                          label, index, pet.size_px, pet.devicePixelRatioF())

    started = time.perf_counter()
    with tempfile.TemporaryDirectory(prefix='diandian-check-', dir=str(output)) as temporary:
        scratch = Path(temporary).resolve()
        assert scratch.parent == output  # Cleanup is confined to this known test directory.
        content_root = scratch / 'editable-project'
        content_root.mkdir()
        baseline = load_project(ROOT)
        resources = {'motions.png', 'curious.png', 'flight.png', 'diandian.ico'}
        resources.update(item['file'] for item in baseline['sounds'] + baseline['frames'])
        for relative in resources:
            target = asset_path(content_root, relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(asset_path(ROOT, relative), target)
        save_project(content_root, baseline)
        ini = scratch / 'preferences.ini'
        initial = QSettings(str(ini), QSettings.IniFormat)
        initial.setValue('muted', True)
        initial.setValue('lively', False)
        initial.sync()
        bird = make_bird(ini)
        try:
            assert bird.windowFlags() & Qt.WindowStaysOnTopHint
            assert len(bird.scaled) == 16 + len(baseline['frames']) and len(bird.flight_scaled) == 8
            assert len(bird.findChildren(QSoundEffect)) == 1 and len(bird.calls) >= 5
            for obsolete in ('frames', 'flight_frames', 'mirrored', 'flight_mirrored', 'sounds'):
                assert not hasattr(bird, obsolete), 'Obsolete resident resource: ' + obsolete
            pixels = sum(p.width() * p.height() * p.depth() // 8 for p in bird.scaled + bird.flight_scaled)
            assert all(p.width() <= round(210 * bird.devicePixelRatioF()) for p in bird.scaled + bird.flight_scaled)
            checked.append('display-sized resources; no old full-size or mirrored arrays; one sound player')

            for action in ('pet', 'walk', 'eat', 'curious', 'bite', 'rock'):
                item = bird.project['actions'][action]
                expected = set(item['frames'])
                bird.start_action(action)
                seen = set()
                for sample in range(round(item['duration'] * 30) + 1):
                    now = bird.started + sample / 30
                    seen.add(bird.frame_index(now))
                    assert bird.animated_sprite(now).hasAlphaChannel()
                    assert len(bird.blends) <= 8
                assert expected <= seen, (action, seen)
                if not item['loop']:
                    last = item['frames'][-1]
                    assert bird.frame_pair(bird.started + item['duration'])[:2] == (last, last)
            assert len(bird.blends) == 8
            checked.append('configured actions including bite/rock cover all poses; nonloops finish on final pose; blend cache bounded')

            # A changing crest must remain one opaque pose, including editor previews.
            # Also cover repeated frames and a retained legacy blend, which previously
            # became translucent in the preview's two SourceOver draw calls.
            # Use synthetic in-memory sequences: users may reorder or remove any
            # original pair in the editor, and imported extension frames are optional.
            crest_cases = [('eat', 2, 3), ('eat', 3, 0), ('curious', 8, 9),
                           ('bite', 18, 19) if len(bird.scaled) > 19 else ('bite', 12, 15),
                           ('rock', 20, 21) if len(bird.scaled) > 21 else ('rock', 9, 10)]
            repeated_cases = [('curious', 9, 9), ('pet', 2, 2)]
            original_project = bird.project
            probe_id = 'crest_regression_probe'
            bird.project = copy.deepcopy(original_project)
            try:
                for action, old, new in crest_cases + repeated_cases + [('pet', 0, 2)]:
                    item = {'label': '冠羽回归', 'kind': 'frames', 'frames': [old, new],
                            'fps': 4, 'duration': 1, 'loop': False,
                            'motion': original_project['actions'].get(action, {}).get('motion', 'none')}
                    bird.project['actions'][probe_id] = item
                    for fraction in (.25, .5, .75):
                        elapsed = fraction / item['fps']
                        first, second, weight = action_pair(item, elapsed)
                        bird.start_action(probe_id)
                        bird.started, bird.direction = 0.0, -1
                        sprite = bird.animated_sprite(elapsed)
                        if (action, old, new) in crest_cases:
                            assert first == second and weight == 0, (action, fraction, first, second, weight)
                            assert first in (old, new)
                            if fraction != .5:
                                assert first == (old if fraction < .5 else new)
                            assert sprite.cacheKey() == bird.scaled[first].cacheKey(), 'Crest transition must use an original whole pose.'
                            cache = {}
                            assert pose_sprite(bird.scaled, first, second, weight, cache).cacheKey() == sprite.cacheKey()
                            assert not cache, 'Single-pose crest transitions must not create alpha blends.'
                        recorder = Recorder()
                        bird.draw_bird(recorder, elapsed)
                        assert recorder.calls == [(QPointF(0, 0), sprite.cacheKey())]
                        size = bird.scaled[0].width() / bird.scaled[0].devicePixelRatio()
                        # These bounds put the preview transform at unit scale and origin.
                        bounds = QRectF(-size*.2, -size*.21, size*1.4, size*1.3)
                        preview_recorder = Recorder()
                        bird.draw_preview(preview_recorder, bounds, bird.project, probe_id, elapsed,
                                          bird.scaled, bird.flight_scaled)
                        assert len(preview_recorder.calls) == 1, 'Editor preview must draw one complete sprite.'
                        if first == second:
                            assert preview_recorder.calls[0][1] == sprite.cacheKey()
                        images = []
                        for preview in (False, True):
                            image = QImage(bird.scaled[0].size(), QImage.Format_RGBA8888)
                            image.setDevicePixelRatio(bird.scaled[0].devicePixelRatio())
                            image.fill(Qt.transparent)
                            painter = QPainter(image)
                            painter.setRenderHint(QPainter.SmoothPixmapTransform)
                            if preview:
                                bird.draw_preview(painter, bounds, bird.project, probe_id, elapsed,
                                                  bird.scaled, bird.flight_scaled)
                            else:
                                apply_motion(painter, item['motion'], elapsed, size)
                                painter.drawPixmap(0, 0, sprite)
                            painter.end()
                            images.append(image)
                        assert images[0] == images[1], 'Preview pixels differ from the actual sprite: ' + action
                        if first == second:
                            alpha = images[1].constBits().asstring(images[1].byteCount())[3::4]
                            assert max(alpha) >= 250, ('Repeated/selected poses must not fade to 75% opacity.',
                                                       action, fraction, max(alpha))
            finally:
                bird.project = original_project
                bird.mode = 'idle'
            checked.append('crest transitions use a single whole pose; preview draws once and matches runtime pixels without repeated-frame fading')

            for direction in (-1, 1):
                bird.mode = 'idle'
                bird.direction = direction
                bird.start_action('fly')
                seen = set()
                for sample in range(120):
                    recorder = Recorder()
                    bird.draw_bird(recorder, bird.started + sample / 60)
                    assert recorder.calls == [(QPointF(-30, -30), bird.flight_scaled[bird.flight_index].cacheKey())], \
                        'Flight must draw exactly one complete bird, without a standing overlay.'
                    seen.add(bird.flight_index)
                assert seen == set(range(8))
            for phase in range(8):
                bird.mode, bird.flight_index, bird.flight_steps = 'fly', phase, []
                bird.flight_next_at = time.monotonic() + 0.05
                bird.start_action('land')
                next_frame_at = bird.flight_next_at
                assert bird.flight_index == phase
                bird.start_action('fly')
                assert bird.flight_index == phase and bird.flight_next_at == next_frame_at
                bird.start_action('land')
                bird.advance_flight(next_frame_at + 1)
                assert bird.mode == 'idle' and bird.flight_index == 0
            bird.start_action('stretch')
            bird.advance_flight(bird.started + 3)
            assert bird.mode == 'idle' and bird.flight_index == 0
            bird.move(100, 100)
            for repeat in range(4):
                was_landing = bird.mode == 'land'
                pose = bird.flight_index
                QTest.mousePress(bird, Qt.LeftButton, pos=QPoint(80, 120))
                if was_landing:
                    assert bird.mode == 'land' and bird.flight_index == pose
                before = bird.pos()
                mouse_move(bird)
                assert bird.mode == 'fly' and bird.dragged and bird.pos() == before
                if was_landing:
                    assert bird.flight_index == pose
                bird.tick()
                assert bird.pos() == before + QPoint(30, 0)
                QTest.mouseRelease(bird, Qt.LeftButton, pos=QPoint(110, 120))
                assert bird.mode == 'land' and bird.drag_origin is None and bird.drag_target is None
            bird.advance_flight(bird.started + 1)
            assert bird.mode == 'idle' and bird.flight_index == 0
            checked.append('actual single-draw flight in both directions; every-phase landing; repeated mouse drags')

            # No sound is audible: exercise real playback with the player volume set to zero.
            bird.muted = bird.sleeping = False
            bird.set_volume(0.0)
            statuses = []
            bird.sound.statusChanged.connect(lambda: statuses.append(int(bird.sound.status())))
            for index, cancel in [(0, bird.toggle_mute), (1, bird.toggle_sleep)]:
                bird.call(index, force=True)
                assert bird.pending_call or bird.sound.isPlaying()
                cancel()
                assert not bird.pending_call and not bird.sound.isPlaying()
                wait_for(lambda: bird.sound.status() == QSoundEffect.Ready, 'Canceled audio did not finish loading.')
                bird.sound_ready()
                assert not bird.pending_call and not bird.sound.isPlaying()
                cancel()
            for index, clip in enumerate(bird.calls):
                bird.call(index, force=True)
                assert Path(bird.sound.source().toLocalFile()).resolve() == asset_path(content_root, clip['file'])
                wait_for(bird.sound.isPlaying, 'Audio failed to load and start: ' + clip['file'])
                assert bird.sound.status() == QSoundEffect.Ready and not bird.pending_call
                assert bird.sound.volume() == 0.0
                bird.stop_call()
            assert int(QSoundEffect.Loading) in statuses and int(QSoundEffect.Ready) in statuses
            # Explicitly cover a delayed ready callback after mute/sleep is already active.
            for flag in ('muted', 'sleeping'):
                setattr(bird, flag, True)
                bird.pending_call = True
                bird.sound_ready()
                assert not bird.pending_call and not bird.sound.isPlaying()
                setattr(bird, flag, False)
            checked.append(f'{len(bird.calls)} real silent audio loads/playbacks; pending audio canceled by mute and sleep')

            count = len(bird.calls)
            bird.call_bag, bird.last_call = [], None
            rotation = [bird.next_call_index() for _ in range(count * 40)]
            assert all(set(rotation[i:i + count]) == set(range(count)) for i in range(0, len(rotation), count))
            assert all(a != b for a, b in zip(rotation, rotation[1:]))
            for explicit in range(count):
                bird.call(explicit, force=True)
                bird.stop_call()
                group = [bird.next_call_index() for _ in range(count)]
                assert set(group) == set(range(count)) and group[0] != explicit
            checked.append('forty complete nonrepeating call groups; explicit selection resets rotation without immediate repeat')

            bird.muted = True
            bird.sleeping = False
            bird.mode = 'idle'
            QTest.mouseClick(bird, Qt.LeftButton, pos=QPoint(80, 120))
            assert bird.click_timer.isActive()
            bird.toggle_sleep()
            QTest.qWait(QApplication.doubleClickInterval() + 30)
            assert bird.sleeping and bird.mode == 'idle' and not bird.click_timer.isActive()
            bird.toggle_sleep()
            QTest.mouseClick(bird, Qt.LeftButton, pos=QPoint(80, 120))
            bird.interact('eat')
            QTest.qWait(QApplication.doubleClickInterval() + 30)
            assert bird.mode == 'eat' and not bird.click_timer.isActive()
            menu = bird.menu()
            assert [a.text() for a in menu.actions() if a.menu()] == ['互动', '动作', '声音', '大小与位置']
            assert any(a.text() == '编辑点点…' for a in menu.actions())
            assert any(a.text() == '更新 v1…' for a in menu.actions())
            bird.click_timer.start(20)
            menu.aboutToShow.emit()
            assert not bird.click_timer.isActive()
            menu.aboutToHide.emit()
            menu.deleteLater()
            app.sendPostedEvents(None, QEvent.DeferredDelete)
            menus_before = len(bird.findChildren(QMenu))
            for _ in range(20):
                bird.refresh_tray_menu()
            app.sendPostedEvents(None, QEvent.DeferredDelete)
            assert len(bird.findChildren(QMenu)) == menus_before
            bird.mode, bird.sleeping, bird.until = 'idle', False, 0
            bird.tick()
            assert bird.timer.interval() == 50
            bird.sleeping = True
            bird.tick()
            assert bird.timer.interval() == 250
            bird.sleeping = False
            checked.append('click-to-sleep/eat/menu regressions; tray menus remain bounded; idle/sleep timer rates')

            for size in (120, 210, 150):
                bird.resize_pet(size)
                check_builtin_pose_edges(bird)
                assert not bird.blends
                bird.home()
                assert QApplication.primaryScreen().availableGeometry().contains(bird.geometry())
                for direction in (-1, 1):
                    bird.direction = direction
                    for mode, phase in [('idle', 0), ('fly', 2), ('fly', 4), ('fly', 6), ('curious', 0.9), ('bite', .6), ('rock', .6)]:
                        bird.mode, bird.sleeping, bird.blink_until = mode, False, 0
                        bird.flight_index, bird.flight_next_at = int(phase), float('inf')
                        bird.started = time.monotonic() - phase
                        dpr = bird.devicePixelRatioF()
                        image = QImage(round(bird.width() * dpr), round(bird.height() * dpr), QImage.Format_RGBA8888)
                        image.setDevicePixelRatio(dpr)
                        image.fill(Qt.transparent)
                        painter = QPainter(image)
                        painter.translate(36, 65)
                        bird.draw_bird(painter, time.monotonic())
                        painter.end()
                        alpha = image.constBits().asstring(image.byteCount())[3::4]
                        width, height = image.width(), image.height()
                        assert min(alpha) == 0 and max(alpha) > 0, (size, direction, mode)
                        border = alpha[:width] + alpha[-width:] + alpha[::width] + alpha[width - 1::width]
                        assert max(border) == 0, 'Clipped silhouette: ' + str((size, direction, mode, phase))
                        assert bird.scaled[0].devicePixelRatio() == dpr
            checked.append('three sizes and both directions render transparent silhouettes; all stock normal/flight pose canvases have clear borders')

            # Reject edits before they can replace the on-disk or running project.
            before_invalid = (content_root / 'project.json').read_bytes()
            invalid_edits = []
            for bad_path in ('../escape.wav', str(asset_path(content_root, baseline['sounds'][0]['file'])), 'calls/missing.wav'):
                invalid = copy.deepcopy(baseline)
                invalid['sounds'][0]['file'] = bad_path
                invalid_edits.append(invalid)
            invalid = copy.deepcopy(baseline)
            invalid['actions']['bite']['frames'] = [16 + len(baseline['frames'])]
            invalid_edits.append(invalid)
            invalid = copy.deepcopy(baseline)
            invalid['actions']['bite']['fps'] = float('nan')
            invalid_edits.append(invalid)
            invalid = copy.deepcopy(baseline)
            invalid['interactions']['click'] = 'missing-action'
            invalid_edits.append(invalid)
            for outline in ([], [[0,0],[0,1],[8192,8192]], [[0,0],[0,1],[float('nan'),1]]):
                invalid = copy.deepcopy(baseline)
                invalid['frames'][0]['outline'] = outline
                invalid_edits.append(invalid)
            for invalid in invalid_edits:
                try:
                    bird.apply_project(invalid)
                except ValueError:
                    pass
                else:
                    raise AssertionError('Invalid project edit was accepted.')
                assert (content_root / 'project.json').read_bytes() == before_invalid
                assert bird.project == baseline
            checked.append('escaping/absolute/missing assets, invalid frame/action references and NaN rejected without replacing project')

            customized = copy.deepcopy(baseline)
            customized['interactions'].update(click='bite', double_click='rock')
            customized['actions']['bite']['label'] = '测试咬手'
            bird.apply_project(customized)
            bird.timer.stop()
            bird.next_action = bird.next_call = float('inf')
            QTest.mouseClick(bird, Qt.LeftButton, pos=QPoint(80, 120))
            QTest.qWait(QApplication.doubleClickInterval() + 30)
            assert bird.mode == 'bite'
            QTest.mouseDClick(bird, Qt.LeftButton, pos=QPoint(80, 120))
            assert bird.mode == 'rock'
            assert load_project(content_root) == customized
            reloaded = make_bird(scratch / 'reload.ini')
            assert reloaded.project == customized
            reloaded.hide()
            disabled = copy.deepcopy(customized)
            for sound in disabled['sounds']:
                sound['enabled'] = False
            bird.apply_project(disabled)
            bird.muted = False
            bird.call(force=True)
            bird.play_action('pet')
            assert bird.calls == [] and not bird.pending_call and not bird.sound.isPlaying()
            assert bird.mode == 'pet'
            bird.apply_project(baseline)
            bird.timer.stop()
            bird.next_action = bird.next_call = float('inf')
            checked.append('edited click/double-click mappings dispatch; configuration survives reconstruction; all sounds can be disabled')

            bird.mode = 'idle'
            bird.resize_pet(120)
            bird.set_volume(0.15)
            bird.muted, bird.auto_calls, bird.lively = True, True, False
            bird.move(80, 70)
            expected_position = bird.pos()
            bird.save_settings()
            bird.settings.sync()
            bird.hide()
            restored = make_bird(ini)
            assert restored.size_px == 120 and abs(restored.volume - 0.15) < 0.001
            assert restored.muted and restored.auto_calls and not restored.lively
            assert restored.pos() == expected_position
            restored.hide()
            restored.settings.setValue('position', QPoint(-100000, 100000))
            restored.settings.setValue('size', 999)
            restored.settings.sync()
            recovered = make_bird(ini)
            assert recovered.size_px == 150
            assert QApplication.primaryScreen().availableGeometry().contains(recovered.geometry())
            assert Path(recovered.settings.fileName()).resolve().parent == scratch
            assert recovered.settings.status() == QSettings.NoError
            checked.append('temporary INI persistence, invalid-size fallback, and off-screen position recovery')

            # Exercise the real editor against the isolated project; no desktop/user data changes.
            from editor import ProjectEditor
            from PyQt5.QtWidgets import QFileDialog
            editor = ProjectEditor(bird)
            editor.audio.setVolume(0.0)
            editor.preview.timer.stop()
            def editor_error(title, error):
                raise AssertionError(title + ': ' + str(error))
            editor.fail = editor_error
            choose_file, choose_files = QFileDialog.getOpenFileName, QFileDialog.getOpenFileNames
            try:
                editor.sounds.setCurrentRow(0)
                editor.sound_name.setText('测试叫声改名')
                assert editor.draft['sounds'][0]['label'] == '测试叫声改名'
                selected_action = next(editor.actions.item(i) for i in range(editor.actions.count())
                                       if editor.actions.item(i).data(Qt.UserRole) == 'bite')
                editor.actions.setCurrentItem(selected_action)
                editor.fps.setValue(7.5)
                assert editor.draft['actions']['bite']['fps'] == 7.5
                editor.click_action.setCurrentIndex(editor.click_action.findData('bite'))
                editor.double_action.setCurrentIndex(editor.double_action.findData('rock'))
                assert editor.save()
                assert bird.project['sounds'][0]['label'] == '测试叫声改名'
                assert load_project(content_root)['actions']['bite']['fps'] == 7.5

                # Native import entry points, with only their file chooser replaced.
                original_count = len(editor.draft['sounds'])
                QFileDialog.getOpenFileName = lambda *a, **k: (str(asset_path(content_root, baseline['sounds'][0]['file'])), 'WAV')
                editor.import_sound(False)
                assert len(editor.draft['sounds']) == original_count + 1
                imported_sound = editor.draft['sounds'][-1]
                assert asset_path(content_root, imported_sound['file']).is_file()
                frame_file = scratch / 'input-pose.png'
                pose = QImage(32, 48, QImage.Format_ARGB32)
                pose.fill(Qt.transparent)
                pose.setPixelColor(16, 24, Qt.black)
                assert pose.save(str(frame_file))
                original_count = len(editor.draft['frames'])
                QFileDialog.getOpenFileNames = lambda *a, **k: ([str(frame_file)], 'PNG')
                editor.import_frame()
                assert len(editor.draft['frames']) == original_count + 1
                assert editor.draft['actions']['bite']['frames'][-1] == 15 + len(editor.draft['frames'])
                assert editor.save()
                assert len(bird.scaled) == 16 + len(bird.project['frames'])
                assert validate_project(load_project(content_root), content_root) == bird.project
            finally:
                QFileDialog.getOpenFileName, QFileDialog.getOpenFileNames = choose_file, choose_files
                editor.draft = copy.deepcopy(editor.saved)
                editor.close()
                bird.apply_project(baseline)
                bird.timer.stop()
            checked.append('visual editor renames sounds, changes action speed and triggers, imports WAV/transparent PNG and saves live')
        finally:
            for pet in birds:
                pet.timer.stop()
                pet.cancel_click()
                pet.stop_call()
                pet.tray.hide()
                pet.close()
            app.sendPostedEvents(None, QEvent.DeferredDelete)

    from update_checks import run as check_updates
    checked.extend(check_updates(app, output))
    report = {'result': 'PASS', 'version': VERSION, 'checks': checked,
              'initial_texture_bytes': pixels, 'elapsed_seconds': round(time.perf_counter() - started, 3),
              'audio_note': 'Playback was tested with volume zero; no human listening assessment.'}
    (output / 'self-test.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    if sys.stdout:
        print(json.dumps(report, ensure_ascii=False))
    return report
