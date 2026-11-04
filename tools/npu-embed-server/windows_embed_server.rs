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

/// The prompt field `@images` asks for several pictures at once: the text is their paths separated by a tab. The reply is one line
/// per picture, in the same order. The pictures are decoded in parallel and run through the NPU in step (`embed_rgb_pipelined`),
/// which saves context switches: about 20% faster per picture than one at a time (see docs/benchmarks/2026-11-gemma-vs-e5.md).
const IMAGES_REQUEST: &str = "@images";

/// Pictures that share one pipelined call. The measured gain is the same from 2 to 4.
const IMAGE_GROUP: usize = 3;

fn embed_images(model: &mut EmbeddingGemma2, paths: &[String]) -> Vec<Result<Vec<f32>, String>> {
    let decoded: Vec<Result<(Vec<u8>, usize, usize), String>> = std::thread::scope(|s| {
        let handles: Vec<_> = paths
            .iter()
            .map(|p| {
                s.spawn(move || {
                    let rgb = image::open(p).map_err(|e| format!("cannot read the image: {e}"))?.to_rgb8();
                    let (w, h) = (rgb.width() as usize, rgb.height() as usize);
                    Ok((rgb.into_raw(), w, h))
                })
            })
            .collect();
        handles.into_iter().map(|h| h.join().unwrap_or_else(|_| Err("the decoder panicked".into()))).collect()
    });
    let mut results: Vec<Option<Result<Vec<f32>, String>>> = (0..paths.len()).map(|_| None).collect();
    let mut work = Vec::new();
    for (i, d) in decoded.into_iter().enumerate() {
        match d {
            Ok((rgb, w, h)) => work.push((i, rgb, w, h)),
            Err(e) => results[i] = Some(Err(e)),
        }
    }
    if !work.is_empty() {
        let mut it = work.into_iter();
        let run = model.embed_rgb_pipelined(IMAGE_GROUP, || it.next(), |i, r| results[i] = Some(r.map_err(|e| e.to_string())));
        if let Err(e) = run {
            for r in results.iter_mut().filter(|r| r.is_none()) {
                *r = Some(Err(e.to_string()));
            }
        }
    }
    results.into_iter().map(|r| r.unwrap_or_else(|| Err("no result".into()))).collect()
}

fn reply_line(result: &Result<Vec<f32>, String>) -> String {
    match result {
        Ok(vector) => {
            let mut reply = String::with_capacity(vector.len() * 12 + 2);
            reply.push('V');
            for v in vector {
                reply.push(' ');
                reply.push_str(&v.to_string());
            }
            reply
        }
        Err(e) => format!("ERR\t{}", e.replace(['\n', '\t', '\r'], " ")),
    }
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
        if prompt == Some(IMAGES_REQUEST) {
            let paths: Vec<String> = text.split('\t').map(unescape).collect();
            for result in embed_images(&mut model, &paths) {
                writeln!(out, "{}", reply_line(&result))?;
            }
            out.flush()?;
            continue;
        }
        let result = if prompt == Some(IMAGE_REQUEST) {
            embed_image(&mut model, &unescape(text))
        } else {
            model.embed_text(&unescape(text), prompt).map_err(|e| e.to_string())
        };
        writeln!(out, "{}", reply_line(&result))?;
        out.flush()?;
    }
    Ok(())
}
