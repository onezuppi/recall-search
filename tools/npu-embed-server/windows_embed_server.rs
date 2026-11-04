// SPDX-License-Identifier: Apache-2.0

//! Long-lived text embedder for Recall Search, built as an example of `taconite-embeddinggemma2`.
//!
//! ```text
//! windows_embed_server <bundle>
//! ```
//!
//! Protocol, one UTF-8 line each way. The model stays loaded until stdin closes.
//!
//! - stderr, once: `READY dim=768 model_load_ms=<n>`
//! - request: `<prompt>\t<text>`. `<prompt>` is a task prompt of the model (`SearchQuery`, `Document`; see
//!   `windows_cli prompts`) or `-` for none. In `<text>` a backslash is `\\`, a newline `\n`, a tab `\t`, a CR `\r`.
//! - image request: prompt `@image`, text is the absolute path of a JPEG or PNG file. The reply is the same as for a text.
//! - response: `V <f32> <f32> ...` (the vector, shortest round-trip decimal form) or `ERR\t<message>`.

use std::io::{self, BufRead, Write};
use std::path::Path;
use std::time::Instant;
use taconite_embeddinggemma2::EmbeddingGemma2;

fn unescape(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut chars = s.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            out.push(c);
            continue;
        }
        match chars.next() {
            Some('n') => out.push('\n'),
            Some('t') => out.push('\t'),
            Some('r') => out.push('\r'),
            Some('\\') => out.push('\\'),
            Some(other) => {
                out.push('\\');
                out.push(other);
            }
            None => out.push('\\'),
        }
    }
    out
}

/// The prompt field `@image` asks for the embedding of the picture file whose path is the text (JPEG and PNG).
const IMAGE_REQUEST: &str = "@image";

fn embed_image(model: &mut EmbeddingGemma2, path: &str) -> Result<Vec<f32>, String> {
    let rgb = image::open(path).map_err(|e| format!("cannot read the image: {e}"))?.to_rgb8();
    let (w, h) = (rgb.width() as usize, rgb.height() as usize);
    model.embed_rgb(rgb.as_raw(), w, h).map_err(|e| e.to_string())
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<String> = std::env::args().collect();
    if args.len() != 2 {
        return Err("usage: windows_embed_server <bundle>".into());
    }
    let start = Instant::now();
    let mut model = EmbeddingGemma2::load(Path::new(&args[1]))?;
    eprintln!("READY dim=768 model_load_ms={:.1}", start.elapsed().as_secs_f64() * 1000.0);

    let stdout = io::stdout();
    let mut out = stdout.lock();
    for line in io::stdin().lock().lines() {
        let line = line?;
        let (prompt, text) = line.split_once('\t').unwrap_or(("-", ""));
        let prompt = if prompt == "-" || prompt.is_empty() { None } else { Some(prompt) };
        let result = if prompt == Some(IMAGE_REQUEST) {
            embed_image(&mut model, &unescape(text))
        } else {
            model.embed_text(&unescape(text), prompt).map_err(|e| e.to_string())
        };
        match result {
            Ok(vector) => {
                let mut reply = String::with_capacity(vector.len() * 12 + 2);
                reply.push('V');
                for v in &vector {
                    reply.push(' ');
                    reply.push_str(&v.to_string());
                }
                writeln!(out, "{reply}")?;
            }
            Err(e) => writeln!(out, "ERR\t{}", e.replace(['\n', '\t', '\r'], " "))?,
        }
        out.flush()?;
    }
    Ok(())
}
