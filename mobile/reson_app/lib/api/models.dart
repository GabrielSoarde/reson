// DTOs mirroring src/Soundpad/Api/Dto/StateDto.cs.
//
// Kept as hand-rolled classes (no codegen) — small surface, easy to grep,
// no build_runner step. Backend uses camelCase JSON via PropertyNamingPolicy.
// Field names here MUST match those camelCase keys exactly.

class GridPosition {
  final int col;
  final int row;
  const GridPosition(this.col, this.row);

  factory GridPosition.fromJson(Map<String, dynamic> j) =>
      GridPosition((j['col'] as num).toInt(), (j['row'] as num).toInt());

  Map<String, dynamic> toJson() => {'col': col, 'row': row};

  @override
  bool operator ==(Object other) =>
      other is GridPosition && other.col == col && other.row == row;

  @override
  int get hashCode => Object.hash(col, row);
}

class GridLayout {
  final int cols;
  final int rows;
  const GridLayout(this.cols, this.rows);

  factory GridLayout.fromJson(Map<String, dynamic> j) =>
      GridLayout((j['cols'] as num).toInt(), (j['rows'] as num).toInt());
}

class SoundEntryDto {
  final String id;
  final String file;
  final String label;
  final String color;
  final String? icon;
  final GridPosition? position;
  final bool missing;
  final int playCount;
  final DateTime? lastPlayedAt;
  final int volume;

  const SoundEntryDto({
    required this.id,
    required this.file,
    required this.label,
    required this.color,
    required this.icon,
    required this.position,
    required this.missing,
    required this.playCount,
    required this.lastPlayedAt,
    required this.volume,
  });

  factory SoundEntryDto.fromJson(Map<String, dynamic> j) => SoundEntryDto(
        id: j['id'] as String,
        file: j['file'] as String,
        label: (j['label'] as String?) ?? '',
        color: (j['color'] as String?) ?? '#3b82f6',
        icon: j['icon'] as String?,
        position: j['position'] == null
            ? null
            : GridPosition.fromJson(j['position'] as Map<String, dynamic>),
        missing: (j['missing'] as bool?) ?? false,
        playCount: (j['playCount'] as num?)?.toInt() ?? 0,
        lastPlayedAt: j['lastPlayedAt'] == null
            ? null
            : DateTime.tryParse(j['lastPlayedAt'] as String),
        volume: (j['volume'] as num?)?.toInt() ?? 100,
      );

  SoundEntryDto copyWith({
    GridPosition? position,
    bool clearPosition = false,
    int? volume,
  }) {
    return SoundEntryDto(
      id: id,
      file: file,
      label: label,
      color: color,
      icon: icon,
      position: clearPosition ? null : (position ?? this.position),
      missing: missing,
      playCount: playCount,
      lastPlayedAt: lastPlayedAt,
      volume: volume ?? this.volume,
    );
  }
}

/// A board (page) of sounds. Mirrors src/Soundpad/Api/Dto board fields.
class BoardDto {
  final String id;
  final String name;
  final String color;

  const BoardDto({
    required this.id,
    required this.name,
    required this.color,
  });

  factory BoardDto.fromJson(Map<String, dynamic> j) => BoardDto(
        id: j['id'] as String,
        name: (j['name'] as String?) ?? '',
        color: (j['color'] as String?) ?? '#3b82f6',
      );
}

class StateDto {
  final String? audioDevice;
  final String? audioDeviceName;
  final String? monitorDevice;
  final String? monitorDeviceName;
  final bool monitorEnabled;
  final bool normalizeEnabled;
  final String? micDevice;
  final String? micDeviceName;
  final List<String> availableOutputDevices;
  final List<String> availableInputDevices;
  final int volume;
  final GridLayout grid;
  final List<SoundEntryDto> sounds;
  final String? nowPlaying;
  final bool authRequired;
  final List<BoardDto> boards;
  final String? activeBoardId;

  const StateDto({
    required this.audioDevice,
    required this.audioDeviceName,
    required this.monitorDevice,
    required this.monitorDeviceName,
    required this.monitorEnabled,
    required this.normalizeEnabled,
    required this.micDevice,
    required this.micDeviceName,
    required this.availableOutputDevices,
    required this.availableInputDevices,
    required this.volume,
    required this.grid,
    required this.sounds,
    required this.nowPlaying,
    required this.authRequired,
    required this.boards,
    required this.activeBoardId,
  });

  factory StateDto.fromJson(Map<String, dynamic> j) => StateDto(
        audioDevice: j['audioDevice'] as String?,
        audioDeviceName: j['audioDeviceName'] as String?,
        monitorDevice: j['monitorDevice'] as String?,
        monitorDeviceName: j['monitorDeviceName'] as String?,
        monitorEnabled: (j['monitorEnabled'] as bool?) ?? false,
        normalizeEnabled: (j['normalizeEnabled'] as bool?) ?? true,
        micDevice: j['micDevice'] as String?,
        micDeviceName: j['micDeviceName'] as String?,
        availableOutputDevices: (j['availableOutputDevices'] as List<dynamic>?)
                ?.cast<String>() ??
            const [],
        availableInputDevices: (j['availableInputDevices'] as List<dynamic>?)
                ?.cast<String>() ??
            const [],
        volume: (j['volume'] as num?)?.toInt() ?? 50,
        grid: GridLayout.fromJson(j['grid'] as Map<String, dynamic>),
        sounds: ((j['sounds'] as List<dynamic>?) ?? const [])
            .map((e) => SoundEntryDto.fromJson(e as Map<String, dynamic>))
            .toList(),
        nowPlaying: j['nowPlaying'] as String?,
        authRequired: (j['authRequired'] as bool?) ?? true,
        boards: ((j['boards'] as List<dynamic>?) ?? const [])
            .map((e) => BoardDto.fromJson(e as Map<String, dynamic>))
            .toList(),
        activeBoardId: j['activeBoardId'] as String?,
      );

  /// The currently active board, or null if none resolves.
  BoardDto? get activeBoard {
    for (final b in boards) {
      if (b.id == activeBoardId) return b;
    }
    return boards.isNotEmpty ? boards.first : null;
  }
}

/// A placement that can be sent to /api/grid/layout. position == null clears it.
class LayoutPlacement {
  final String id;
  final GridPosition? position;
  const LayoutPlacement(this.id, this.position);

  Map<String, dynamic> toJson() => {
        'id': id,
        'position': position?.toJson(),
      };
}
