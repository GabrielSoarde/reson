import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';

import '../api/api_client.dart';
import '../theme.dart';

/// Multi-file audio upload screen.
///
/// Behavior mirrors the web FAB:
/// - Pick files via file_picker (FileType.audio with extension filter)
/// - Upload SEQUENTIALLY (one at a time) — same rationale: keeps memory
///   pressure low on phones and the progress text honest
/// - On finish, pop back to home; HomeScreen will refetch /api/state on resume
///   (and the backend already broadcast libraryChanged per file)
class UploadScreen extends StatefulWidget {
  const UploadScreen({super.key, required this.api});
  final ApiClient api;

  @override
  State<UploadScreen> createState() => _UploadScreenState();
}

class _UploadScreenState extends State<UploadScreen> {
  List<PlatformFile> _files = [];
  bool _uploading = false;
  int _doneCount = 0;
  int _failCount = 0;
  String? _currentName;

  Future<void> _pick() async {
    // FileType.audio with the explicit extension allowlist matches
    // SoundLibrary.AllowedExtensions on the server. The picker will already
    // pre-filter, so the user sees only valid files in their library.
    final picked = await FilePicker.platform.pickFiles(
      allowMultiple: true,
      type: FileType.custom,
      allowedExtensions: const ['mp3', 'wav', 'ogg', 'flac'],
      withData: false, // don't pull bytes into memory; we stream from path
    );
    if (picked == null) return;
    setState(() {
      _files = picked.files.where((f) => f.path != null).toList();
      _doneCount = 0;
      _failCount = 0;
      _currentName = null;
    });
  }

  Future<void> _upload() async {
    if (_files.isEmpty || _uploading) return;
    setState(() {
      _uploading = true;
      _doneCount = 0;
      _failCount = 0;
    });
    for (var i = 0; i < _files.length; i++) {
      final f = _files[i];
      if (!mounted) return;
      setState(() => _currentName = f.name);
      try {
        await widget.api.uploadSound(f.path!, f.name);
        if (!mounted) return;
        setState(() => _doneCount++);
      } catch (e) {
        if (!mounted) return;
        setState(() => _failCount++);
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('${f.name}: ${_short(e)}')),
        );
      }
    }
    if (!mounted) return;
    setState(() {
      _uploading = false;
      _currentName = null;
    });
    final ok = _doneCount;
    final fail = _failCount;
    String msg;
    if (ok > 0 && fail == 0) {
      msg = ok == 1 ? 'Som adicionado' : '$ok sons adicionados';
    } else if (ok > 0 && fail > 0) {
      msg = '$ok ok, $fail falharam';
    } else {
      msg = 'Nenhum som adicionado';
    }
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(msg)));
    if (fail == 0) {
      Navigator.of(context).pop();
    }
  }

  String _short(Object e) {
    final s = e.toString();
    return s.length > 80 ? '${s.substring(0, 77)}…' : s;
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: AppColors.background,
      appBar: AppBar(title: const Text('Adicionar sons')),
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ElevatedButton.icon(
                onPressed: _uploading ? null : _pick,
                icon: const Icon(Icons.audio_file),
                label: const Text('Escolher arquivos'),
              ),
              const SizedBox(height: 16),
              if (_files.isEmpty)
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 32),
                  child: Center(
                    child: Text(
                      'Formatos suportados: .mp3, .wav, .ogg, .flac',
                      style: TextStyle(color: AppColors.muted),
                    ),
                  ),
                )
              else
                Expanded(
                  child: ListView.builder(
                    itemCount: _files.length,
                    itemBuilder: (context, i) {
                      final f = _files[i];
                      final isCurrent = _currentName == f.name && _uploading;
                      final isDone = i < _doneCount;
                      final state = isCurrent
                          ? Icons.upload
                          : isDone
                              ? Icons.check_circle
                              : Icons.audio_file;
                      return ListTile(
                        leading: Icon(state,
                            color: isDone
                                ? AppColors.success
                                : isCurrent
                                    ? AppColors.accent
                                    : AppColors.muted),
                        title: Text(f.name,
                            maxLines: 1, overflow: TextOverflow.ellipsis),
                        subtitle: Text(_fmtBytes(f.size),
                            style: const TextStyle(color: AppColors.muted, fontSize: 12)),
                        trailing: _uploading
                            ? null
                            : IconButton(
                                icon: const Icon(Icons.close, color: AppColors.muted),
                                onPressed: () => setState(() => _files.removeAt(i)),
                              ),
                      );
                    },
                  ),
                ),
              if (_files.isNotEmpty) ...[
                if (_uploading)
                  Padding(
                    padding: const EdgeInsets.only(top: 8, bottom: 12),
                    child: Text(
                      'Enviando ${_doneCount + _failCount + 1}/${_files.length}…',
                      textAlign: TextAlign.center,
                      style: const TextStyle(color: AppColors.muted),
                    ),
                  ),
                ElevatedButton.icon(
                  onPressed: _uploading ? null : _upload,
                  icon: _uploading
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white),
                        )
                      : const Icon(Icons.cloud_upload),
                  label: Text(_uploading ? 'Enviando…' : 'Enviar ${_files.length}'),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }

  String _fmtBytes(int bytes) {
    if (bytes < 1024) return '$bytes B';
    if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KB';
    return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
  }
}
