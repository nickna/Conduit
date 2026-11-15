-- Apply only to task-owned phase0_extended / phase0_extended_smoke databases.
UPDATE "VirtualKeys" SET "RateLimitRpm"=1000000,"RateLimitRpd"=1000000
WHERE "KeyName"='Native AOT parity key';
DELETE FROM "AsyncTasks" WHERE "Id" IN
('task_native_aot_parity','task_native_aot_image_notification','task_native_aot_video_notification');
UPDATE "Models" SET "SupportsSpeechToText"=true,
 "InputModalities"='["text","image","audio"]'::jsonb WHERE "Id"=-9004;
UPDATE "ModelIdentifiers" SET "InputModalities"='["text","image","audio"]'::jsonb WHERE "Id"=-9006;
UPDATE "ModelCosts" SET "AudioCostPerMinute"=0.06 WHERE "Id"=-9005;
SELECT json_build_object('fixtureModel',m."Id",'supportsSpeechToText',m."SupportsSpeechToText",
 'audioCostPerMinute',c."AudioCostPerMinute",'inputPerMillion',c."InputCostPerMillionTokens",
 'outputPerMillion',c."OutputCostPerMillionTokens")
FROM "Models" m CROSS JOIN "ModelCosts" c WHERE m."Id"=-9004 AND c."Id"=-9005;
