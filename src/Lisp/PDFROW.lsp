;; PDFROW - импорт страниц многостраничного PDF в виде блоков на слое 0.
;; Листы расставляются в ряд слева направо, масштаб импорта задаётся при запуске.
;; Для каждой страницы:
;;   1) вызывается -PDFIMPORT (страница, масштаб) в точку 0,0;
;;   2) все импортированные объекты собираются в блок (слой 0);
;;   3) блок вставляется и сдвигается вправо от предыдущего листа.
;; Работает в мировой системе координат (WCS).
;; Перед импортом файл проверяется на защиту паролем (признак /Encrypt в PDF).
;; Если импорт страницы не удался, скрипт предлагает остановиться.

(defun pr-unique-name (base / i nm)
  (setq i 1 nm base)
  (while (tblsearch "BLOCK" nm)
    (setq i (1+ i)
          nm (strcat base "_" (itoa i)))
  )
  nm
)

;; Габариты объекта: (мин. точка макс. точка) или nil
(defun pr-bbox (ent / obj mn mx r)
  (setq obj (vlax-ename->vla-object ent))
  (setq r (vl-catch-all-apply 'vla-getboundingbox (list obj 'mn 'mx)))
  (if (vl-catch-all-error-p r)
    nil
    (list (vlax-safearray->list mn) (vlax-safearray->list mx))
  )
)


;; Проверка PDF на шифрование (признак /Encrypt в служебной части файла).
;; Возвращает T, если признак найден. Это не гарантирует пароль на открытие:
;; PDF с паролем только на редактирование открывается без пароля.
(defun pr-encrypted-p (file / f ln found)
  (setq found nil)
  (setq f (open file "r"))
  (if f
    (progn
      (while (and (not found) f)
        (setq ln (vl-catch-all-apply 'read-line (list f)))
        (cond
          ((or (vl-catch-all-error-p ln) (null ln)) (setq f (progn (close f) nil)))
          ((vl-string-search "/Encrypt" ln) (setq found T))
        )
      )
      (if f (close f))
    )
  )
  found
)

(defun shiza:run-pdfrow (/ *error* oldLayer oldLayers oldFiledia oldCmdecho oldOsmode
                   file p1 scl pg1 pg2 gap pg cursorX baseY last e typ ss cnt
                   bname ref bb mn mx done okc stuck ans)

  ;; Восстановление переменных при ошибке/Esc
  (defun pr-restore ()
    (if oldLayer   (setvar "CLAYER" oldLayer))
    (if oldFiledia (setvar "FILEDIA" oldFiledia))
    (if oldCmdecho (setvar "CMDECHO" oldCmdecho))
    (if oldOsmode  (setvar "OSMODE" oldOsmode))
    (if oldLayers  (vl-catch-all-apply 'setvar (list "PDFIMPORTLAYERS" oldLayers)))
  )
  (defun *error* (msg)
    (while (> (getvar "CMDACTIVE") 0) (command))
    (pr-restore)
    (if (and msg
             (not (wcmatch (strcase msg) "*CANCEL*,*ОТМЕН*,*EXIT*,*QUIT*")))
      (princ (strcat "\nОшибка: " msg))
    )
    (princ)
  )

  (vl-load-com)
  (setq file (getfiled "Выберите многостраничный PDF" "" "pdf" 0))
  ;; Проверка защиты паролем
  (if (and file (pr-encrypted-p file))
    (progn
      (princ "\nВНИМАНИЕ: PDF защищён (найден признак шифрования), возможно, он закрыт паролем.")
      (princ "\nЕсли пароль нужен для открытия, импорт не сработает.")
      (initget "Остановить Продолжить")
      (setq ans (getkword "\nОстановить скрипт? [Остановить/Продолжить] <Остановить>: "))
      (if (/= ans "Продолжить")
        (progn
          (princ "\nСкрипт остановлен. Снимите пароль с PDF и запустите снова.")
          (setq file nil)
        )
      )
    )
  )
  (if file
    (progn
      (initget 6)
      (setq pg1 (getint "\nПервая страница <1>: "))
      (if (not pg1) (setq pg1 1))
      (initget 7)
      (setq pg2 (getint "\nПоследняя страница: "))
      (initget 6)
      (setq scl (getreal "\nМасштаб импорта <1>: "))
      (if (not scl) (setq scl 1.0))
      (setq gap (getdist "\nРасстояние между листами <0>: "))
      (if (not gap) (setq gap 0.0))
      (setq p1 (getpoint "\nТочка вставки (левый нижний угол первого листа): "))
    )
  )
  (if (and file p1 pg2 (>= pg2 pg1))
    (progn
      ;; Сохраняем и настраиваем системные переменные
      (setq oldLayer   (getvar "CLAYER")
            oldFiledia (getvar "FILEDIA")
            oldCmdecho (getvar "CMDECHO")
            oldOsmode  (getvar "OSMODE")
            oldLayers  (vl-catch-all-apply 'getvar (list "PDFIMPORTLAYERS"))
      )
      (if (vl-catch-all-error-p oldLayers) (setq oldLayers nil))
      (setvar "CMDECHO" 0)
      (setvar "FILEDIA" 0)
      (setvar "OSMODE" 0)
      (setvar "CLAYER" "0")
      ;; 2 = все импортируемые объекты на текущий слой
      (vl-catch-all-apply 'setvar (list "PDFIMPORTLAYERS" 2))

      (setq cursorX (car p1)
            baseY   (cadr p1)
            pg      pg1
            done    0)
      (while (<= pg pg2)
        (setq last (entlast))
        ;; Импорт страницы: файл, страница, точка вставки, масштаб, поворот
        (command "_.-PDFIMPORT" "_F" file pg "0,0,0" scl 0)
        ;; Если команда осталась активной (например, запросила пароль или
        ;; другой неожиданный ввод), отменяем её. Enter здесь нельзя: он
        ;; повторил бы последнюю команду.
        (setq okc 0 stuck nil)
        (while (and (> (getvar "CMDACTIVE") 0) (< okc 10))
          (command)
          (setq stuck T okc (1+ okc))
        )

        ;; Собираем импортированные объекты
        (setq ss (ssadd)
              cnt 0
              e (if last (entnext last) (entnext)))
        (while e
          (setq typ (cdr (assoc 0 (entget e))))
          (if (not (member typ '("ATTRIB" "VERTEX" "SEQEND")))
            (progn (ssadd e ss) (setq cnt (1+ cnt)))
          )
          (setq e (entnext e))
        )

        (if (or stuck (= cnt 0))
          (progn
            (princ (strcat "\nСтраница " (itoa pg) ": не импортирована"
                           " (возможно, PDF закрыт паролем или такой страницы нет)."))
            ;; импортированный мусор от прерванной команды не обрабатываем
            (initget "Остановить Продолжить")
            (setq ans (getkword "\nОстановить скрипт? [Остановить/Продолжить] <Остановить>: "))
            (if (/= ans "Продолжить")
              (setq pg pg2)
            )
          )
          (progn
            (if (and (= cnt 1)
                     (= (cdr (assoc 0 (entget (ssname ss 0)))) "INSERT"))
              ;; Импорт уже вернул один блок (включена настройка "как блок")
              (setq ref (ssname ss 0))
              ;; Иначе собираем объекты в блок и вставляем его
              (progn
                (setq bname (pr-unique-name (strcat "ЛИСТ_" (itoa pg))))
                (command "_.-BLOCK" bname "0,0,0" ss "")
                (entmake (list '(0 . "INSERT")
                               (cons 2 bname)
                               (cons 8 "0")
                               (cons 10 '(0.0 0.0 0.0))))
                (setq ref (entlast))
              )
            )
            ;; Ставим лист на своё место в ряду
            (setq bb (pr-bbox ref))
            (if bb
              (progn
                (setq mn (car bb) mx (cadr bb))
                (vla-move (vlax-ename->vla-object ref)
                          (vlax-3d-point mn)
                          (vlax-3d-point (list cursorX baseY 0.0)))
                (setq cursorX (+ cursorX (- (car mx) (car mn)) gap))
              )
              (princ "\nНе удалось определить габариты листа, сдвиг пропущен.")
            )
            (setq done (1+ done))
          )
        )
        (setq pg (1+ pg))
      )
      (pr-restore)
      (princ (strcat "\nИмпортировано листов: " (itoa done)))
    )
    (if (and file p1 pg2)
      (princ "\nПоследняя страница меньше первой.")
    )
  )
  (princ)
)
