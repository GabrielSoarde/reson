import 'package:flutter/material.dart';

import '../api/api_client.dart';
import '../storage/config_store.dart';
import '../theme.dart';

/// Fallback for environments where camera access is broken or the QR is on
/// paper. User enters IP, port, token; we validate by hitting /api/state.
///
/// We intentionally don't try to be clever about IPv6 / hostnames — the QR
/// flow handles those. This form is the lowest-common-denominator backup.
class ManualEntryScreen extends StatefulWidget {
  const ManualEntryScreen({super.key});

  @override
  State<ManualEntryScreen> createState() => _ManualEntryScreenState();
}

class _ManualEntryScreenState extends State<ManualEntryScreen> {
  final _formKey = GlobalKey<FormState>();
  final _ip = TextEditingController();
  final _port = TextEditingController(text: '8080');
  final _token = TextEditingController();
  bool _busy = false;

  @override
  void dispose() {
    _ip.dispose();
    _port.dispose();
    _token.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    setState(() => _busy = true);
    final base = 'http://${_ip.text.trim()}:${_port.text.trim()}';
    final ok = await probeConnection(base, _token.text.trim());
    if (!mounted) return;
    if (!ok) {
      setState(() => _busy = false);
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Não foi possível conectar. Verifique IP, porta e token.')),
      );
      return;
    }
    await ConfigStore().save(PairedConfig(base, _token.text.trim()));
    if (!mounted) return;
    Navigator.of(context).pop(true);
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: AppColors.background,
      appBar: AppBar(title: const Text('Pareamento manual')),
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Form(
            key: _formKey,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                const Text(
                  'Encontre IP, porta e token no PC em Settings ou no QR exibido.',
                  style: TextStyle(color: AppColors.muted, fontSize: 13),
                ),
                const SizedBox(height: 24),
                TextFormField(
                  controller: _ip,
                  decoration: const InputDecoration(
                    labelText: 'IP do PC',
                    hintText: '192.168.1.100',
                  ),
                  keyboardType: TextInputType.url,
                  autocorrect: false,
                  validator: (v) {
                    final t = v?.trim() ?? '';
                    if (t.isEmpty) return 'Obrigatório';
                    // Lightweight IPv4/hostname check — backend will reject if bad.
                    if (!RegExp(r'^[A-Za-z0-9._\-]+$').hasMatch(t)) return 'IP/host inválido';
                    return null;
                  },
                ),
                const SizedBox(height: 16),
                TextFormField(
                  controller: _port,
                  decoration: const InputDecoration(labelText: 'Porta'),
                  keyboardType: TextInputType.number,
                  validator: (v) {
                    final n = int.tryParse(v?.trim() ?? '');
                    if (n == null || n < 1 || n > 65535) return 'Porta inválida';
                    return null;
                  },
                ),
                const SizedBox(height: 16),
                TextFormField(
                  controller: _token,
                  decoration: const InputDecoration(labelText: 'Token'),
                  autocorrect: false,
                  validator: (v) {
                    if ((v ?? '').trim().isEmpty) return 'Obrigatório';
                    return null;
                  },
                ),
                const SizedBox(height: 32),
                ElevatedButton(
                  onPressed: _busy ? null : _submit,
                  child: _busy
                      ? const SizedBox(
                          width: 22,
                          height: 22,
                          child: CircularProgressIndicator(strokeWidth: 2.5, color: Colors.white),
                        )
                      : const Text('Conectar'),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
